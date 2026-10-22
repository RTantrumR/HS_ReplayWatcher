"""Prove the typed codec against real traffic: decode + re-encode every packet of a recording, byte-compare.

    python tools/check_proto_roundtrip.py recordings/<file>.jsonl

Exit code 1 if any packet of a known message type does not round-trip.
"""
from __future__ import annotations

import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver.packets import packet_name  # noqa: E402
from hsserver.proto import decode, encode, schema  # noqa: E402
from hsserver.recording import load  # noqa: E402


def main() -> None:
    rec = load(sys.argv[1])
    ok, bad, skipped, unknown_fields = Counter(), Counter(), Counter(), Counter()
    first_bad = {}
    for p in rec.packets:
        msg = "PegasusGame." + packet_name(p.type)
        if msg not in schema() or not p.body:
            skipped[p.name] += 1
            continue
        try:
            obj = decode(msg, p.body)
            again = encode(msg, obj)
        except Exception as e:  # noqa: BLE001 - report every failure kind
            bad[p.name] += 1
            first_bad.setdefault(p.name, f"#{p.seq}: {type(e).__name__}: {e}")
            continue
        if again == p.body:
            ok[p.name] += 1
        else:
            bad[p.name] += 1
            first_bad.setdefault(p.name, f"#{p.seq}: {len(p.body)} -> {len(again)} bytes")
        if "_unknown" in obj:
            unknown_fields[p.name] += 1
    for name in sorted(set(ok) | set(bad)):
        flag = "" if not bad[name] else f"  FAIL x{bad[name]} ({first_bad[name]})"
        unk = f"  ({unknown_fields[name]} with unknown fields)" if unknown_fields[name] else ""
        print(f"{name:<36} ok x{ok[name]:<6}{flag}{unk}")
    if skipped:
        print("skipped (no schema or empty body):", dict(skipped))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
