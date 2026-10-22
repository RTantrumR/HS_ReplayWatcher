"""Serve recorded games back to the real client.

    python tools/replay_server.py [--recordings recordings] [--port 3724] [--speed 1.0] [-v]
    python tools/replay_server.py recordings/<file>.jsonl ...

Without a file it serves the recordings folder as a library: the plugin's Replays page (BG lobby) starts it
on Watch and loads that game with "load <id>" before joining. With a file, that game is loaded at start and the
library is its folder (a Watch on the page then uses this server: the plugin leaves a server it didn't start
alone).
In game: + / - change speed (0 = paused), Enter / Backspace step, the panel (F10) jumps.
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import os
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver.library import Library  # noqa: E402
from hsserver.replay import USER_UI, serve  # noqa: E402

# Chatty types that would drown the log: UserUI (mouse broadcast), AchievementProgress, PlayerRealTimeBattlefieldRaces.
DEFAULT_QUIET = {USER_UI, 52, 36}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("recording", nargs="?", help="a recording to load at start (default: library mode only)")
    ap.add_argument("--recordings", default=str(Path(__file__).resolve().parents[1] / "recordings"),
                    help="folder served as the library (default: the repo's recordings/)")
    ap.add_argument("--saved", help="the saved games' folder: games there can be loaded too")
    ap.add_argument("--logs", help="the folder of games converted from Power.log: games there can be loaded too")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=3724)
    ap.add_argument("--speed", type=float, default=1.0, help="starting timing multiplier (2 = twice as fast, 0 = paused)")
    ap.add_argument("--control-port", type=int, default=3725, help="UDP port for the plugin's keys, panel and Replays page")
    ap.add_argument("-v", "--verbose", action="store_true", help="log every S2C packet")
    ap.add_argument("--parent-pid", type=int, help="exit when this process (HS, which started us) is gone")
    args = ap.parse_args()

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(name)s %(message)s", datefmt="%H:%M:%S")
    if args.recording:
        path = Path(args.recording).resolve()
        library = Library(path.parent)
        library.preload(path)
    else:
        library = Library(Path(args.recordings).resolve(), Path(args.saved).resolve() if args.saved else None,
                          Path(args.logs).resolve() if args.logs else None)
    if args.parent_pid:
        threading.Thread(target=exit_with_parent, args=(args.parent_pid,), daemon=True).start()
    try:
        asyncio.run(serve(library, args.host, args.port, args.speed, DEFAULT_QUIET, args.control_port))
    except KeyboardInterrupt:
        pass


def exit_with_parent(pid: int) -> None:
    """Wait for process pid to end, then end this one (HS started us and may die without stopping us)."""
    import ctypes
    synchronize, infinite = 0x00100000, 0xFFFFFFFF
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(synchronize, False, pid)
    if handle:
        kernel32.WaitForSingleObject(handle, infinite)
        kernel32.CloseHandle(handle)
    logging.getLogger("replay").info("parent process %d is gone; exiting", pid)
    os._exit(0)


if __name__ == "__main__":
    main()
