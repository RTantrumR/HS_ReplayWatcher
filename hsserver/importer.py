"""Import replays other players shared, and Power.log files, into the Imported folder, checked before anything
else reads them.

    python -I -m hsserver.importer --into <Imported> [--known <folder>]... <file>...

The replay server sends a recording's packets straight to the Hearthstone client, so a received file is never
trusted or copied as it is:
- it is opened read-only, sized first (the file, then, while unpacking, the unpacked text: no gzip bombs), then
  read line by line under length and count caps;
- every line must be a JSON object of a known kind (connect, reconnect, packet, note) with only that kind's fields,
  each of its type and, for strings, its pattern; the first line must be the connect;
- every packet's type must be one the client speaks, its body valid base64 of the stated length, and the body must
  decode against schema.json with no field the schema doesn't know and re-encode to the same bytes; strings in it
  have no control characters;
- the stored copy is written anew from what was decoded (the connection's passwords, address and handle and the
  handshake's machine details blanked, as hsserver.share does), then catalogued with the catalog's own reader: it
  must be a Battlegrounds game with a watched player, and every card id its row shows must be a plain card id.
A Power.log (the client's text log) is size-capped and converted (hsserver.powerlog); its games then go through
the same checks. Prints one JSON line per result: {"file", "result": imported|exists|refused, "reason"} and for a
game read, "id", "hero" (card id), "place", "start" ("YYYY-MM-DD HH:MM").
"""
from __future__ import annotations

import argparse
import base64
import binascii
import contextlib
import datetime as dt
import gzip
import json
import math
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path

from .packets import PACKET_NAMES
from .proto import decode, encode, schema
from .share import scrub_line

MAX_FILE = 32 << 20  # a shared recording (gzipped); real ones are ~1 MB
MAX_LOG = 1 << 30  # a Power.log (text, a whole session)
MAX_UNPACKED = 128 << 20  # real ones: up to ~11 MB
MAX_LINE = 8 << 20  # real ones: up to ~0.75 MB
MAX_LINES = 200_000  # real ones: up to ~5,500
MAX_BODY = 4 << 20  # real ones: up to ~0.56 MB
MAX_STRING = 4096

NUM, INT, STR, BOOL, OPT_STR = "num", "int", "str", "bool", "str?"
CONNECTION = {
    "seq": INT, "ms": NUM, "kind": STR, "address": STR, "port": INT, "gameHandle": INT, "clientHandle": INT,
    "auroraPassword": STR, "version": STR, "mission": INT, "brawlLibraryItemId": INT, "spectatorPassword": OPT_STR,
    "spectatorMode": BOOL, "reconnecting": BOOL, "nextGameType": STR, "nextFormatType": STR, "nextMissionId": INT,
    "nextSpectator": BOOL, "nextReconnect": BOOL, "clientVersion": STR, "source": STR, "logFile": STR,
    "buildNumber": INT,
}
FIELDS = {
    "connect": CONNECTION,
    "reconnect": CONNECTION,
    "packet": {"seq": INT, "ms": NUM, "kind": STR, "dir": STR, "type": INT, "len": INT, "body": STR},
    "note": {"seq": INT, "ms": NUM, "kind": STR, "text": STR},
}
REQUIRED = {
    "connect": ("seq", "ms", "nextGameType"),
    "reconnect": ("seq", "ms"),
    "packet": ("seq", "ms", "dir", "type", "len", "body"),
    "note": ("seq", "ms", "text"),
}
PASSWORD = re.compile(r"[\x20-\x7e]{0,256}")
PATTERNS = {
    "kind": re.compile(r"connect|reconnect|packet|note"),
    "address": re.compile(r"[0-9A-Za-z.:\-]{0,64}"),
    "auroraPassword": PASSWORD,
    "spectatorPassword": PASSWORD,
    "version": re.compile(r"[0-9A-Za-z._\-]{0,32}"),
    "clientVersion": re.compile(r"[0-9A-Za-z._\-]{0,32}"),
    "nextGameType": re.compile(r"[A-Z0-9_]{1,48}"),
    "nextFormatType": re.compile(r"[A-Z0-9_]{1,48}"),
    "source": re.compile(r"[a-z]{1,16}"),
    "logFile": re.compile(r"[\w .()\-]{1,96}"),
    "dir": re.compile(r"S2C|C2S"),
    "text": re.compile(r"block -?\d{1,10} -?\d{1,10} -?\d{1,10}|combat_done"),
}
CONTROL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
CARD_ID = re.compile(r"[A-Za-z0-9_]{1,64}")
NAME = re.compile(r"(\d{8}_\d{6}_(?:game|log)[0-9A-Za-z_]{1,40})(?: \(\d+\))?\.jsonl(?:\.gz)?")


class Refused(Exception):
    """A file that isn't a replay (or a Power.log) the importer will store; the message says why."""


def _check_value(kind: str, key: str, typ: str, v) -> None:
    if typ == BOOL:
        ok = isinstance(v, bool)
    elif typ == INT:
        ok = isinstance(v, int) and not isinstance(v, bool) and abs(v) < 1 << 63
    elif typ == NUM:
        ok = (isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) and 0 <= v < 1e13)
    elif typ == OPT_STR:
        ok = v is None or isinstance(v, str)
    else:
        ok = isinstance(v, str)
    if not ok:
        raise Refused(f"a {kind} line's {key} is not a {typ}")
    pattern = PATTERNS.get(key)
    if isinstance(v, str) and pattern is not None and not pattern.fullmatch(v):
        raise Refused(f"a {kind} line's {key} isn't of the expected form")


def _check_strings(obj, path: str = "") -> None:
    """No field the schema doesn't know, and strings without control characters, anywhere in a decoded body."""
    if isinstance(obj, dict):
        if "_unknown" in obj:
            raise Refused(f"a packet has fields the game's protocol doesn't have ({path or 'top'})")
        for k, v in obj.items():
            _check_strings(v, f"{path}.{k}" if path else k)
    elif isinstance(obj, list):
        for v in obj:
            _check_strings(v, path)
    elif isinstance(obj, str):
        if len(obj) > MAX_STRING or CONTROL.search(obj):
            raise Refused(f"a packet has an odd text in {path}")


def check_line(obj, first: bool) -> dict:
    """One recording line checked; returns the line to store (only its known fields, the body re-encoded)."""
    if not isinstance(obj, dict):
        raise Refused("a line is not a JSON object")
    kind = obj.get("kind")
    fields = FIELDS.get(kind) if isinstance(kind, str) else None
    if fields is None:
        raise Refused("a line of an unknown kind")
    if first != (kind == "connect"):
        raise Refused("the connection line isn't first" if first else "a second connection line")
    for key in obj:
        if key not in fields:
            raise Refused(f"a {kind} line has an unknown field {key!r}"[:120])
    for key in REQUIRED[kind]:
        if key not in obj:
            raise Refused(f"a {kind} line has no {key}")
    for key, v in obj.items():
        _check_value(kind, key, fields[key], v)
    out = {k: obj[k] for k in fields if k in obj}
    if kind == "packet":
        ptype = obj["type"]
        if ptype not in PACKET_NAMES:
            raise Refused(f"a packet of an unknown type {ptype}")
        if len(obj["body"]) > (MAX_BODY * 4) // 3 + 4:
            raise Refused("a packet is too large")
        try:
            body = base64.b64decode(obj["body"], validate=True)
        except (binascii.Error, ValueError):
            raise Refused("a packet's body is not valid base64") from None
        if len(body) != obj["len"]:
            raise Refused("a packet's length doesn't match its body")
        if body:
            msg = "PegasusGame." + PACKET_NAMES[ptype]
            if msg not in schema():
                raise Refused(f"a packet of a type with no known layout ({PACKET_NAMES[ptype]})")
            try:
                decoded = decode(msg, body)
                again = encode(msg, decoded)
            except Refused:
                raise
            except Exception:  # noqa: BLE001 - any failure to decode a hostile body is a refusal
                raise Refused(f"a {PACKET_NAMES[ptype]} packet doesn't decode") from None
            if again != body:
                raise Refused(f"a {PACKET_NAMES[ptype]} packet doesn't decode exactly")
            _check_strings(decoded)
            out["body"] = base64.b64encode(again).decode("ascii")
        out = scrub_line(out)
    elif kind in ("connect", "reconnect"):
        out = scrub_line(out)
    return out


def _read_text(path: Path, gz: bool):
    """The file's lines, read-only, under the unpacked size, line length and line count caps."""
    total = 0
    count = 0
    with path.open("rb") as raw:
        stream = gzip.GzipFile(fileobj=raw, mode="rb") if gz else raw
        try:
            while True:
                line = stream.readline(MAX_LINE + 1)
                if not line:
                    return
                total += len(line)
                if total > MAX_UNPACKED:
                    raise Refused("too large once unpacked")
                if len(line) > MAX_LINE:
                    raise Refused("a line is too long")
                count += 1
                if count > MAX_LINES:
                    raise Refused("too many lines")
                try:
                    yield line.decode("utf-8")
                except UnicodeDecodeError:
                    raise Refused("not text (UTF-8)") from None
        except (OSError, EOFError, gzip.BadGzipFile):
            raise Refused("a damaged or incomplete gzip file") from None


def rebuild(path: Path, gz: bool, out: Path) -> None:
    """Check every line of a recording and write the checked copy to out (gzipped)."""
    first = True
    lines = 0
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=6) as fh:
        for text in _read_text(path, gz):
            text = text.strip()
            if not text:
                continue
            try:
                obj = json.loads(text)
            except (json.JSONDecodeError, RecursionError):
                raise Refused("a line is not valid JSON") from None
            fh.write(json.dumps(check_line(obj, first), separators=(",", ":"), ensure_ascii=False) + "\n")
            first = False
            lines += 1
    if lines < 2:
        raise Refused("no game in it")


def check_row(path: Path):
    """The checked copy as the catalog reads it (returned): a Battlegrounds game with a watched player, plain card
    ids."""
    from .catalog import read_game  # its packets are already checked
    try:
        g = read_game(path)
    except Exception:  # noqa: BLE001
        raise Refused("the game can't be read") from None
    if g is None:
        raise Refused("not a Battlegrounds game with a player to watch")
    cards = [g.hero, *g.hero_powers, *g.trinkets, *g.hp_quest] + [m[0] for m in g.board + g.hand]
    for card in cards:
        if card and not CARD_ID.fullmatch(str(card)):
            raise Refused("a card id of an unexpected form")
    return g


def game_id(path: Path, out: Path) -> str:
    """The id to store it under: the shared file's own name (a browser's " (1)" taken off), else the time it was
    received and the game's handle."""
    m = NAME.fullmatch(path.name)
    if m:
        return m.group(1)
    handle = 0
    with gzip.open(out, "rt", encoding="utf-8") as fh:
        handle = json.loads(fh.readline()).get("gameHandle", 0) or 0
    when = dt.datetime.fromtimestamp(path.stat().st_mtime)
    return f"{when:%Y%m%d_%H%M%S}_game{int(handle) & 0x7FFFFFFF}"


def exists(gid: str, folders: list[Path]) -> Path | None:
    for folder in folders:
        for suffix in (".jsonl.gz", ".jsonl"):
            if (folder / f"{gid}{suffix}").is_file():
                return folder
    return None


def import_recording(path: Path, into: Path, known: list[Path], gz: bool) -> dict:
    tmp = into / f".import-{os.getpid()}.tmp.gz"
    try:
        rebuild(path, gz, tmp)
        g = check_row(tmp)
        gid = game_id(path, tmp)
        # The game as the page names it (hero card id, place, start "YYYY-MM-DD HH:MM" from the id).
        game = {"id": gid, "hero": g.hero, "place": g.place, "start": f"{gid[:4]}-{gid[4:6]}-{gid[6:8]} {gid[9:11]}:{gid[11:13]}"}
        where = exists(gid, [into, *known])
        if where is not None:
            return {"result": "exists", **game, "reason": "already in " + where.name}
        os.replace(tmp, into / f"{gid}.jsonl.gz")
        return {"result": "imported", **game}
    finally:
        tmp.unlink(missing_ok=True)


def import_log(path: Path, into: Path, known: list[Path]) -> list[dict]:
    """A Power.log's Battlegrounds games, converted in a folder of their own, then checked like any recording."""
    from . import powerlog
    work = Path(tempfile.mkdtemp(prefix="hsrw-import-"))
    try:
        # The converter prints its progress; stdout carries only the results here.
        with contextlib.redirect_stdout(sys.stderr):
            try:
                games, _ = powerlog.parse([path])
            except Exception:  # noqa: BLE001
                raise Refused("the Power.log can't be read") from None
            enums_build = powerlog.Enums().build
        results = []
        for g in games:
            if not g.is_battlegrounds or not g.local_player:
                continue
            with contextlib.redirect_stdout(sys.stderr):
                out = powerlog.write_game(g, work, enums_build)
            if out is None:
                continue
            try:
                r = import_recording(out, into, known, True)
            except Refused as e:
                r = {"result": "refused", "reason": str(e)}
            results.append(r)
        if not results:
            raise Refused("no Battlegrounds game in the Power.log")
        return results
    finally:
        shutil.rmtree(work, ignore_errors=True)


def import_file(path: Path, into: Path, known: list[Path]) -> list[dict]:
    if path.is_symlink() or not path.is_file():
        raise Refused("not a file")
    size = path.stat().st_size
    with path.open("rb") as fh:
        head = fh.read(65536)
    if head[:2] == b"\x1f\x8b":
        if size > MAX_FILE:
            raise Refused("too large for a replay")
        return [import_recording(path, into, known, True)]
    if head.lstrip()[:1] == b"{":
        if size > MAX_FILE * 4:
            raise Refused("too large for a replay")
        return [import_recording(path, into, known, False)]
    if b"GameState." in head or b"PowerTaskList." in head:
        if size > MAX_LOG:
            raise Refused("too large for a Power.log")
        return import_log(path, into, known)
    raise Refused("not a replay or a Power.log")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Import shared replays and Power.log files, checked, into a folder.")
    ap.add_argument("files", nargs="+", type=Path)
    ap.add_argument("--into", type=Path, required=True, help="the Imported folder")
    ap.add_argument("--known", type=Path, action="append", default=[], help="another folder a game may already be in")
    args = ap.parse_args(argv)
    args.into.mkdir(parents=True, exist_ok=True)
    out = sys.stdout
    for path in args.files:
        try:
            results = import_file(path, args.into, args.known)
        except Refused as e:
            results = [{"result": "refused", "reason": str(e)}]
        except Exception as e:  # noqa: BLE001 - one bad file never stops the others
            results = [{"result": "refused", "reason": f"couldn't be read ({type(e).__name__})"}]
        for r in results:
            out.write(json.dumps({"file": path.name, **r}, ensure_ascii=False) + "\n")
            out.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
