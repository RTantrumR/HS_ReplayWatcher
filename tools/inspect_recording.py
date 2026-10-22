"""Summarize or dump a game-server recording made by the HS Replay Watcher plugin.

    python tools/inspect_recording.py recordings/<file>.jsonl            # connect info + packet counts
    python tools/inspect_recording.py <file> --timeline 60               # first 60 packets in order
    python tools/inspect_recording.py <file> --dump 12                   # raw protobuf tree of packet seq 12
    python tools/inspect_recording.py <file> --dump-type Handshake       # first packet of that type
"""
from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver.packets import decode_raw, format_raw, packet_name  # noqa: E402
from hsserver.recording import RecordedPacket, load  # noqa: E402


def dump(p: RecordedPacket, max_lines: int) -> None:
    print(f"#{p.seq} {p.dir} {p.name} ({p.type}) {len(p.body)} bytes @ {p.ms} ms")
    try:
        print(format_raw(decode_raw(p.body), max_lines=max_lines))
    except ValueError as e:
        print(f"  not a protobuf message ({e}): {p.body[:64].hex()}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("recording")
    ap.add_argument("--timeline", type=int, metavar="N")
    ap.add_argument("--dump", type=int, metavar="SEQ")
    ap.add_argument("--dump-type", metavar="NAME")
    ap.add_argument("--max-lines", type=int, default=200)
    args = ap.parse_args()

    rec = load(args.recording)

    if args.dump is not None:
        hits = [p for p in rec.packets if p.seq == args.dump]
        if not hits:
            sys.exit(f"no packet with seq {args.dump}")
        dump(hits[0], args.max_lines)
        return
    if args.dump_type:
        hits = [p for p in rec.packets if p.name == args.dump_type]
        if not hits:
            sys.exit(f"no {args.dump_type} packet")
        dump(hits[0], args.max_lines)
        return

    if rec.connect:
        print("connect:")
        for k, v in rec.connect.items():
            if k not in ("seq", "ms", "kind"):
                print(f"  {k}: {v}")
    else:
        print("connect: (none — recording started mid-game)")

    if rec.packets:
        span = rec.packets[-1].ms - rec.packets[0].ms
        print(f"\n{len(rec.packets)} packets over {span / 1000:.1f} s")
    counts = Counter((p.dir, p.type) for p in rec.packets)
    sizes = Counter()
    for p in rec.packets:
        sizes[(p.dir, p.type)] += len(p.body)
    for (d, t), n in sorted(counts.items(), key=lambda kv: (kv[0][0], -kv[1])):
        print(f"  {d} {t:>4} {packet_name(t):<36} x{n:<6} {sizes[(d, t)]:>10} bytes")

    if args.timeline:
        print()
        for p in rec.packets[:args.timeline]:
            print(f"  #{p.seq:<5} {p.ms:>8} ms  {p.dir}  {p.name:<36} {len(p.body)} bytes")


if __name__ == "__main__":
    main()
