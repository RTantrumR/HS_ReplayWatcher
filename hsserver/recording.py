"""Reader for the JSONL recordings written by the HS Replay Watcher plugin.

A recording is ``<yyyymmdd_hhmmss>_game<handle>.jsonl`` while its game can still be reconnected to, and
``.jsonl.gz`` once the catalog has compressed it (see :func:`compress`). A reconnect (a crash, a disconnect, a
relaunch) carries on in the same file after a ``reconnect`` line; ``seq`` and ``ms`` continue across it.
"""
from __future__ import annotations

import base64
import gzip
import json
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

from .packets import packet_name

log = logging.getLogger("recording")

SUFFIXES = (".jsonl.gz", ".jsonl")


@dataclass
class RecordedPacket:
    seq: int
    ms: int
    dir: str  # "S2C" or "C2S"
    type: int
    body: bytes

    @property
    def name(self) -> str:
        return packet_name(self.type)


@dataclass
class Recording:
    path: Path
    connect: dict | None = None
    packets: list[RecordedPacket] = field(default_factory=list)
    notes: list[dict] = field(default_factory=list)
    reconnects: list[dict] = field(default_factory=list)  # the reconnect lines (seq, ms, connection info)
    bad_lines: int = 0  # lines that weren't whole JSON (a crash cuts the line being written)


def game_id(path: str | Path) -> str:
    """The recording's id: its file name without .jsonl / .jsonl.gz."""
    name = Path(path).name
    for suffix in SUFFIXES:
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return Path(path).stem


def find(folder: str | Path, gid: str) -> Path | None:
    """The recording of game id gid in folder (compressed or not), or None."""
    for suffix in SUFFIXES:
        path = Path(folder) / f"{gid}{suffix}"
        if path.is_file():
            return path
    return None


def recordings(folder: str | Path) -> list[Path]:
    """Every recording in folder, one per id (an uncompressed one wins: it may still be written to)."""
    by_id: dict[str, Path] = {}
    for path in sorted(Path(folder).glob("*.jsonl.gz")) + sorted(Path(folder).glob("*.jsonl")):
        by_id[game_id(path)] = path
    return [by_id[k] for k in sorted(by_id)]


def _open(path: Path):
    return gzip.open(path, "rt", encoding="utf-8") if path.name.endswith(".gz") else path.open(encoding="utf-8")


def watching_another(connection: dict) -> bool:
    """A connect/reconnect line of the client spectating someone else's game on a real server (the recorder
    used to record those; a spectate of a party member's game has the player's own game handle, so it carried on
    in the player's file). The local replay server's joins are spectator joins too, but not this."""
    return (bool(connection.get("spectatorMode")) and connection.get("auroraPassword") != "local"
            and connection.get("address") != "127.0.0.1")


def load(path: str | Path) -> Recording:
    """A recording, without the parts where the client was spectating another player (see watching_another)."""
    path = Path(path)
    rec = Recording(path)
    spectating = False
    with _open(path) as fh:
        for line_no, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                # A crash cuts the line being written short; the recorder starts the next one on a new line.
                rec.bad_lines += 1
                log.warning("%s:%d: not a whole line, skipped", path.name, line_no)
                continue
            kind = obj.get("kind")
            if kind in ("connect", "reconnect"):
                spectating = watching_another(obj)
            if kind == "connect":
                rec.connect = obj
            elif spectating:
                continue
            elif kind == "packet":
                rec.packets.append(RecordedPacket(
                    obj["seq"], obj["ms"], obj["dir"], obj["type"], base64.b64decode(obj["body"])))
            elif kind == "note":
                rec.notes.append(obj)
            elif kind == "reconnect":
                rec.reconnects.append(obj)
    return rec


def compress(path: str | Path) -> Path:
    """Gzip a .jsonl recording beside itself and remove the original once the copy reads back the same."""
    path = Path(path)
    out = path.with_name(path.name + ".gz")
    tmp = out.with_name(out.name + ".tmp")
    data = path.read_bytes()
    with gzip.open(tmp, "wb", compresslevel=6) as fh:
        fh.write(data)
    with gzip.open(tmp, "rb") as fh:
        if fh.read() != data:
            tmp.unlink(missing_ok=True)
            raise OSError(f"compressed copy of {path.name} doesn't match")
    os.replace(tmp, out)
    path.unlink()
    return out
