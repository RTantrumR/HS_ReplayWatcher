"""Prove hsserver.importer: real recordings (and their shareable copies) import; tampered ones are refused.

    python tools/check_import.py <recording>... [--folder DIR]...

Each real recording is imported into a scratch folder, then its hsserver.share copy (the file a player sends), then
the same id again (already there). From the first recording, files are made that a hostile sender could send, and
each must be refused with its reason: an unknown field, an unknown line kind, bad base64, a wrong length, a
protocol field the game doesn't have, a cut body, a control character in a card's name, a card id that would
break the rows file, a game that isn't Battlegrounds, an unknown note, the connection not first, a gzip bomb, a
damaged gzip, and bytes that are no replay at all. Exit code 1 on any wrong result.
"""
from __future__ import annotations

import argparse
import base64
import gzip
import json
import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver.importer import import_file  # noqa: E402
from hsserver.importer import Refused  # noqa: E402
from hsserver.proto import decode, encode  # noqa: E402
from hsserver.share import share  # noqa: E402

POWER_HISTORY = 19


def lines_of(path: Path) -> list[dict]:
    opener = gzip.open if path.name.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as fh:
        return [json.loads(x) for x in fh if x.strip()]


def write(path: Path, lines: list, raw: bytes | None = None) -> Path:
    if raw is not None:
        path.write_bytes(raw)
        return path
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        for x in lines:
            fh.write((x if isinstance(x, str) else json.dumps(x, separators=(",", ":"))) + "\n")
    return path


def result(path: Path, into: Path) -> tuple[str, str]:
    try:
        r = import_file(path, into, [])[0]
    except Refused as e:
        return "refused", str(e)
    return r["result"], r.get("reason", r.get("id", ""))


def first_packet(lines: list[dict], ptype: int) -> int:
    return next(i for i, x in enumerate(lines) if x.get("kind") == "packet" and x["type"] == ptype)


def with_body(line: dict, body: bytes) -> dict:
    return {**line, "body": base64.b64encode(body).decode(), "len": len(body)}


def renamed_entities(lines: list[dict], name: str) -> list[dict]:
    """Every FullEntity's card id in the game's PowerHistory packets replaced (the hero's among them)."""
    out = []
    for x in lines:
        if x.get("kind") == "packet" and x["type"] == POWER_HISTORY:
            ph = decode("PegasusGame.PowerHistory", base64.b64decode(x["body"]))
            touched = False
            for e in ph.get("List", []):
                full = e.get("FullEntity")
                if full and full.get("Name"):
                    full["Name"] = name
                    touched = True
            if touched:
                x = with_body(x, encode("PegasusGame.PowerHistory", ph))
        out.append(x)
    return out


def hostile(lines: list[dict], work: Path) -> list[tuple[str, Path, str]]:
    """(what, file, a word the refusal must contain)."""
    p = first_packet(lines, POWER_HISTORY)
    pkt = lines[p]
    body = base64.b64decode(pkt["body"])
    cases = []

    def case(name, new_lines, expect, raw=None):
        cases.append((name, write(work / f"20990101_000000_game{len(cases)}.jsonl.gz", new_lines, raw), expect))

    case("unknown field", lines[:p] + [{**pkt, "extra": 1}] + lines[p + 1:], "unknown field")
    case("unknown kind", lines + [{"kind": "script", "seq": 1, "ms": 1}], "unknown kind")
    case("bad base64", lines[:p] + [{**pkt, "body": "@@@@"}] + lines[p + 1:], "base64")
    case("wrong length", lines[:p] + [{**pkt, "len": pkt["len"] + 1}] + lines[p + 1:], "length")
    case("protocol field the game hasn't", lines[:p] + [with_body(pkt, body + b"\xf8\x06\x01")] + lines[p + 1:],
         "protocol")
    case("cut body", lines[:p] + [with_body(pkt, body[:-3])] + lines[p + 1:], "decode")
    case("control character in a name", renamed_entities(lines, "BG_\x01X"), "odd text")
    case("card id breaking the rows", renamed_entities(lines, "TB_BaconShop_HERO_01|0|0"), "card id")
    case("not Battlegrounds", [{**lines[0], "nextGameType": "GT_RANKED"}] + lines[1:], "Battlegrounds")
    case("unknown note", lines + [{"kind": "note", "seq": 1, "ms": 1, "text": "rm -rf"}], "form")
    case("connection not first", lines[1:2] + lines[:1] + lines[2:], "first")
    # Valid lines that unpack to ~7 MB each (whitespace inside the JSON): only the unpacked-size cap stops it.
    padded = b'{"kind":"note",' + b" " * (7 << 20) + b'"seq":1,"ms":1,"text":"combat_done"}\n'
    bomb = gzip.compress(json.dumps(lines[0]).encode() + b"\n" + padded * 20, 9)
    case("gzip bomb", None, "too large", raw=bomb)
    good = gzip.compress("".join(json.dumps(x) + "\n" for x in lines).encode())
    case("damaged gzip", None, "gzip", raw=good[: len(good) // 2])
    case("no replay", None, "not a replay", raw=os.urandom(4096))
    return cases


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("recordings", nargs="*", type=Path)
    ap.add_argument("--folder", type=Path, action="append", default=[])
    args = ap.parse_args()
    files = list(args.recordings)
    for f in args.folder:
        files += sorted(f.glob("*.jsonl.gz")) + sorted(f.glob("*.jsonl"))
    seen = set()
    files = [f for f in files if not (f.name.split(".")[0] in seen or seen.add(f.name.split(".")[0]))]  # a game saved is in two folders
    if not files:
        print("no recordings given")
        return 2
    failed = 0
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        into, sent, work = tmp / "Imported", tmp / "sent", tmp / "work"
        for d in (into, sent, work, tmp / "hostile"):
            d.mkdir()
        sample = None
        for f in files:
            got, why = result(f, into)
            copy = sent / f"{f.name.split('.')[0]} (1).jsonl.gz"
            share(f, copy)
            again, why2 = result(copy, into)
            mark = "ok" if got in ("imported", "refused") else "??"
            print(f"{mark} {f.name}: {got} ({why}); its shared copy: {again} ({why2})")
            if got == "imported":
                sample = sample or f
                if again != "exists":
                    failed += 1
                    print("   FAIL: the shared copy of an imported game should be 'exists'")
            elif "Battlegrounds" not in why:
                failed += 1
                print("   FAIL: a real recording refused for something other than not being a BG game")
        if sample is None:
            print("no recording imported; no hostile cases made")
            return 1
        for name, path, expect in hostile(lines_of(sample), work):
            got, why = result(path, tmp / "hostile")
            ok = got == "refused" and expect.lower() in why.lower()
            failed += not ok
            print(f"{'ok' if ok else 'FAIL'} {name}: {got} ({why})")
    print("all as expected" if not failed else f"{failed} wrong")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
