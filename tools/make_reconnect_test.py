"""A made-up reconnect recording, for working on the replay's reconnect handling until real ones exist.

Copies a real recording and cuts two disconnects into it:
  A: 30 s away in the middle of a shop phase
  B: away from 10 s before a fight until 10 s into the next shop (the fight is missed)
Everything recorded while "away" is dropped; at the reconnect a "reconnect" line, a C2S Handshake, the GameSetup,
a PowerHistory with CreateGame + FullEntity of the true state at that moment, and the pending choices/options/
timer/BG side packets (as our snapshot builds them) stand in for what the server sends a rejoining client.

What the server really sends a rejoining client is not known yet (docs/recorder.md); this is our guess at it.

    python tools/make_reconnect_test.py recordings/<real game>.jsonl.gz recordings/<new id>.jsonl
"""
import base64
import gzip
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from hsserver.proto import decode, encode  # noqa: E402
from hsserver.recording import load  # noqa: E402
from hsserver.replay import ReplayData, POWER_HISTORY  # noqa: E402

HANDSHAKE, GAME_SETUP = 168, 16

src, out = Path(sys.argv[1]), Path(sys.argv[2])
rec = load(src)
data = ReplayData(rec)
t0 = next(p.ms for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE)
combats, resume = data.combats, data.combat_resume

def pick_a():
    for k in range(3, len(combats) - 1):
        d = resume[k].point + 8000
        r = d + 30000
        if r < combats[k + 1][0] - 5000:
            return d, r, k
    raise SystemExit("no shop long enough for gap A")

def pick_b(after):
    for k in range(after + 2, len(combats) - 1):
        d = combats[k][0] - 10000
        r = resume[k].point + 10000
        if d > resume[k - 1].point + 2000 and r < combats[k + 1][0] - 5000:
            return d, r, k
    raise SystemExit("no fight for gap B")

da, ra, ka = pick_a()
db, rb, kb = pick_b(ka)
gaps = [(da, ra), (db, rb)]
print(f"gap A: {da/1000:.1f}-{ra/1000:.1f} s (shop after combat {ka}, turn {data.turn_at(da)})")
print(f"gap B: {db/1000:.1f}-{rb/1000:.1f} s (misses combat {kb}, turn {data.turn_at(db)})")

game_setup = next(p.body for p in rec.packets if p.dir == "S2C" and p.type == GAME_SETUP)
handshake = next(p.body for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE)

lines = []
raw = []
opener = gzip.open if src.name.endswith(".gz") else open
with opener(src, "rt", encoding="utf-8") as fh:
    for line in fh:
        line = line.strip()
        if line:
            raw.append(json.loads(line))

def rel(obj):
    return obj["ms"] - t0

def packet(ms, d, ptype, body):
    return {"ms": ms, "kind": "packet", "dir": d, "type": ptype, "len": len(body),
            "body": base64.b64encode(body).decode()}

def reconnect_block(r_rel):
    ms = r_rel + t0 - 400  # the block sits just before the reconnect moment, ahead of the packets kept after it
    meta = {k: v for k, v in raw[0].items() if k not in ("seq", "ms", "kind")}
    meta["reconnecting"] = True
    meta["nextReconnect"] = True
    block = [{"ms": ms, "kind": "reconnect", **meta}, packet(ms, "C2S", HANDSHAKE, handshake),
             packet(ms + 40, "S2C", GAME_SETUP, game_setup)]
    snap = data.rewind_packets(r_rel)
    ph = decode("PegasusGame.PowerHistory", snap[0][1])
    items = ph["List"]
    cg = next(x["ResetGame"]["CreateGame"] for x in items if "ResetGame" in x)
    full = [x for x in items if "FullEntity" in x]
    body = encode("PegasusGame.PowerHistory", {"List": [{"CreateGame": cg}, *full]})
    block.append(packet(ms + 120, "S2C", POWER_HISTORY, body))
    for i, (ptype, b) in enumerate(snap[1:]):
        block.append(packet(ms + 200 + min(i, 190), "S2C", ptype, b))
    return block

for obj in raw:
    if obj.get("kind") == "connect":
        lines.append(obj)
        continue
    r = rel(obj)
    if any(d < r < rr for d, rr in gaps):
        continue
    for d, rr in gaps:
        if lines and rel(lines[-1]) <= rr <= r and not any(x.get("_gap") == rr for x in lines):
            for x in reconnect_block(rr):
                x["_gap"] = rr
                lines.append(x)
    lines.append(obj)

with open(out, "w", encoding="utf-8", newline="\n") as fh:
    for seq, obj in enumerate(lines):
        obj.pop("_gap", None)
        o = {"seq": seq, "ms": obj["ms"], **{k: v for k, v in obj.items() if k not in ("seq", "ms")}}
        fh.write(json.dumps(o, separators=(",", ":")) + "\n")
print(f"wrote {out} ({len(lines)} lines, from {len(raw)})")
