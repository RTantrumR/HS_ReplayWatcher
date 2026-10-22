"""Check that hsserver.share's copies replay the same and carry none of the blanked details.

For each recording: the copy has the same packets (server packets byte-exact, the client's but the handshake too),
the same notes, the same catalog row and the same replay points, and the original's handshake password, device
name and device id appear nowhere in it.

    python tools/check_share.py <recording or folder> [...]
"""
from __future__ import annotations

import base64
import gzip
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver import catalog, recording  # noqa: E402
from hsserver.proto import decode  # noqa: E402
from hsserver.replay import ReplayData  # noqa: E402
from hsserver.share import HANDSHAKE, share  # noqa: E402


def secrets(rec: recording.Recording) -> list[str]:
    out = []
    for p in rec.packets:
        if p.dir == "C2S" and p.type == HANDSHAKE:
            hs = decode("PegasusGame.Handshake", p.body)
            out += [hs.get("Password", ""), (hs.get("Platform") or {}).get("Name", ""),
                    (hs.get("Platform") or {}).get("UniqueDeviceIdentifier", "")]
    out += [str((rec.connect or {}).get(k, "")) for k in ("auroraPassword", "spectatorPassword")]
    return [s for s in out if len(s) >= 4]


def check(path: Path, tmp: Path) -> list[str]:
    problems = []
    out = tmp / (recording.game_id(path) + ".jsonl.gz")
    share(path, out)
    a, b = recording.load(path), recording.load(out)
    if len(a.packets) != len(b.packets):
        problems.append(f"{len(a.packets)} packets became {len(b.packets)}")
    for pa, pb in zip(a.packets, b.packets):
        if (pa.seq, pa.ms, pa.dir, pa.type) != (pb.seq, pb.ms, pb.dir, pb.type):
            problems.append(f"packet {pa.seq} moved or changed type")
            break
        if pa.type != HANDSHAKE and pa.body != pb.body:
            problems.append(f"packet {pa.seq} ({pa.name}) body changed")
            break
    if a.notes != b.notes:
        problems.append("notes changed")
    text = gzip.decompress(out.read_bytes()).decode("utf-8")
    for s in secrets(a):
        if s in text or base64.b64encode(s.encode()).decode() in text:
            problems.append(f"a blanked detail ({len(s)} chars) is still in the copy")
    # The handshake's details sit inside its protobuf body (base64 in the file): read back from the copy.
    for p in b.packets:
        if p.dir == "C2S" and p.type == HANDSHAKE:
            hs = decode("PegasusGame.Handshake", p.body)
            platform = hs.get("Platform") or {}
            if hs.get("Password") or hs.get("ClientHandle") or platform.get("Name") or platform.get("UniqueDeviceIdentifier"):
                problems.append("the copy's handshake still has the player's details")
    ga, gb = catalog.read_game(path), catalog.read_game(out)
    if (ga and catalog.row(ga)) != (gb and catalog.row(gb)):
        problems.append("catalog row differs")
    da, db = ReplayData(a), ReplayData(b)
    if ([(p.vt, p.kind) for p in da.points] != [(p.vt, p.kind) for p in db.points] or da.labels != db.labels
            or da.end != db.end or da.combats != db.combats):
        problems.append("replay points differ")
    return problems


def main() -> int:
    paths = []
    for arg in sys.argv[1:]:
        p = Path(arg)
        paths += recording.recordings(p) if p.is_dir() else [p]
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for path in paths:
            problems = check(path, Path(tmp))
            print(f"{'FAIL' if problems else 'ok  '} {path.name}" + "".join(f"\n     {x}" for x in problems))
            bad += bool(problems)
    print(f"{len(paths) - bad}/{len(paths)} ok")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
