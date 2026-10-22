"""A copy of a recording that can be shared, e.g. attached to a public bug report.

The copy replays the same: only what identifies the player's account and machine is blanked, none of which the
replay server reads (it times the replay from the handshake and plays the server's packets):
- the connect and reconnect lines: the game server's address, the game's session passwords, the client handle;
- the client's handshake: its password, client handle, device name (the motherboard) and device id.

    python -m hsserver.share <recording> <out.jsonl.gz>
"""
from __future__ import annotations

import argparse
import base64
import gzip
import json
from pathlib import Path

from .proto import decode, encode
from .recording import _open

HANDSHAKE = 168
CONNECTION_BLANKS = {"address": "", "auroraPassword": "", "spectatorPassword": "", "clientHandle": 0}


def scrub_line(obj: dict) -> dict:
    """One recording line with the account's and the machine's details blanked."""
    if obj.get("kind") in ("connect", "reconnect"):
        return {**obj, **{k: v for k, v in CONNECTION_BLANKS.items() if k in obj}}
    if obj.get("kind") == "packet" and obj.get("dir") == "C2S" and obj.get("type") == HANDSHAKE:
        hs = decode("PegasusGame.Handshake", base64.b64decode(obj["body"]))
        hs["Password"] = ""
        hs["ClientHandle"] = 0
        platform = hs.get("Platform")
        if platform:
            platform["Name"] = ""
            platform["UniqueDeviceIdentifier"] = ""
        body = encode("PegasusGame.Handshake", hs)
        return {**obj, "body": base64.b64encode(body).decode("ascii"), "len": len(body)}
    return obj


def share(src: str | Path, out: str | Path) -> int:
    """Write the shareable copy of src to out (gzipped); returns the number of lines written. Lines that aren't
    whole JSON (a crash cut them) are left out, as the reader skips them anyway."""
    lines = []
    with _open(Path(src)) as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            lines.append(json.dumps(scrub_line(obj), separators=(",", ":")))
    out = Path(out)
    tmp = out.with_name(out.name + ".tmp")
    with gzip.open(tmp, "wt", encoding="utf-8", compresslevel=6) as fh:
        fh.write("\n".join(lines) + "\n")
    tmp.replace(out)
    return len(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Write a shareable copy of a recording (account and machine details blanked).")
    ap.add_argument("recording")
    ap.add_argument("out", help="the copy, gzipped (.jsonl.gz)")
    args = ap.parse_args(argv)
    print(f"{share(args.recording, args.out)} lines -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
