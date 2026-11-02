"""Check that a reconnect plays once and the replay goes on past it, however late the server answered it.

Copies a real recording and cuts in one drop: half a second after a choice offered with a combat's end (a turn's
trinket pick), away for 20 s, then a "reconnect" line and, DELAY ms after it, the reconnect's burst (GameSetup,
CreateGame + FullEntity of the true state, the open choices and options). Real connects get their first packet
4.8-11.4 s after the line. Each copy is then played by the real ReplaySession to a fake spectator from 3 s before
the drop at x4, for 10 s: the reconnect sequence must play once and the next packet must be past the drop.

    python tools/check_reconnect.py <real recording> [delay ms ...]   (default delays: 40 2000 5000 6000 30000)
"""
from __future__ import annotations

import asyncio
import base64
import gzip
import json
import logging
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver import recording  # noqa: E402
from hsserver.packets import frame  # noqa: E402
from hsserver.proto import decode, encode  # noqa: E402
from hsserver.replay import (ENTITY_CHOICES, HANDSHAKE, POWER_HISTORY, SPECTATOR_HANDSHAKE,  # noqa: E402
                             ReplayData, ReplaySession)

GAME_SETUP = 16
AWAY_MS = 20000.0
PLAY_S = 10.0


def cut_drop(src: Path, out: Path, delay: float) -> None:
    rec = recording.load(src)
    data = ReplayData(rec)
    t0 = next(p.ms for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE)
    ends = [e for _, e in data.combats]
    offered = next((rel for rel, ptype, _, _ in data.plan
                    if ptype == ENTITY_CHOICES and any(0 <= rel - e <= 50 for e in ends)), None)
    if offered is None:
        raise SystemExit(f"{src.name}: no choice offered with a combat's end")
    drop, back = offered + 500, offered + 500 + AWAY_MS
    opener = gzip.open if src.name.endswith(".gz") else open
    with opener(src, "rt", encoding="utf-8") as fh:
        raw = [json.loads(line) for line in fh if line.strip()]

    def packet(ms, d, ptype, body):
        return {"ms": ms, "kind": "packet", "dir": d, "type": ptype, "len": len(body),
                "body": base64.b64encode(body).decode()}

    meta = {k: v for k, v in raw[0].items() if k not in ("seq", "ms", "kind")}
    meta.update(reconnecting=True, nextReconnect=True)
    line = back + t0
    answer = line + delay
    snap = data.rewind_packets(back)
    items = decode("PegasusGame.PowerHistory", snap[0][1])["List"]
    create = next(x["ResetGame"]["CreateGame"] for x in items if "ResetGame" in x)
    state = encode("PegasusGame.PowerHistory", {"List": [{"CreateGame": create}, *(x for x in items if "FullEntity" in x)]})
    burst = [{"ms": line, "kind": "reconnect", **meta},
             packet(line + 120, "C2S", HANDSHAKE, next(p.body for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE)),
             packet(answer, "S2C", GAME_SETUP, next(p.body for p in rec.packets if p.dir == "S2C" and p.type == GAME_SETUP)),
             packet(answer + 80, "S2C", POWER_HISTORY, state)]
    burst += [packet(answer + 160 + i, "S2C", ptype, body) for i, (ptype, body) in enumerate(snap[1:])]
    lines, cut_in = [], False
    for o in raw:
        rel = o["ms"] - t0
        if o.get("kind") != "connect" and drop < rel <= answer + 400:
            continue
        if not cut_in and o.get("kind") != "connect" and rel > answer + 400:
            lines += burst
            cut_in = True
        lines.append(o)
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        for seq, o in enumerate(lines):
            fh.write(json.dumps({"seq": seq, "ms": o["ms"], **{k: v for k, v in o.items() if k not in ("seq", "ms")}},
                                separators=(",", ":")) + "\n")


class Sequences(logging.Handler):
    def __init__(self):
        super().__init__()
        self.played = 0

    def emit(self, record):
        if "the recorded client dropped" in record.getMessage():
            self.played += 1


async def play(path: Path) -> str | None:
    data = ReplayData(recording.load(path))
    gap = data.gaps[0]
    seen = Sequences()
    logging.getLogger("replay").addHandler(seen)
    session = ReplaySession(data, 4, set())
    server = await asyncio.start_server(session.run, "127.0.0.1", 0)
    reader, writer = await asyncio.open_connection("127.0.0.1", server.sockets[0].getsockname()[1])
    writer.write(frame(SPECTATOR_HANDSHAKE, b""))
    await writer.drain()

    async def sink():
        while await reader.read(65536):
            pass

    sinking = asyncio.create_task(sink())
    await asyncio.sleep(0.2)
    session.control("pastpick")
    session.control(f"time {gap.start - 3000:.0f}")
    end = time.monotonic() + PLAY_S
    while time.monotonic() < end:
        if session.waiting_for_combat:
            session.control("combatdone")  # the fake spectator shows combats at once
        await asyncio.sleep(0.05)
    writer.close()
    server.close()
    sinking.cancel()
    logging.getLogger("replay").removeHandler(seen)
    if seen.played != 1 or session.idx <= gap.first:
        return (f"the reconnect sequence played {seen.played}x; next packet {session.idx} "
                f"(the drop's first is {gap.first}); landed at {gap.land / 1000:.1f} s, "
                f"its first packet at {data.plan[gap.first][0] / 1000:.1f} s")
    return None


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    logging.basicConfig(level=logging.WARNING)
    logging.getLogger("replay").setLevel(logging.INFO)
    src = Path(sys.argv[1])
    delays = [float(a) for a in sys.argv[2:]] or [40, 2000, 5000, 6000, 30000]
    failed = 0
    with tempfile.TemporaryDirectory() as tmp:
        for delay in delays:
            out = Path(tmp) / f"drop_{delay:.0f}.jsonl"
            cut_drop(src, out, delay)
            problem = asyncio.run(play(out))
            failed += problem is not None
            print(f"answered {delay / 1000:5.2f} s after the reconnect line: {problem or 'played once, went on'}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
