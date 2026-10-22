"""The replay server's games: the catalog of a recordings folder and the replays loaded from it.

The plugin's Replays page lists the catalog ("catalog <start>") and asks for one game ("load <id>"); building
its ReplayData takes a few seconds, so it runs in a worker thread while the server keeps answering, and the
plugin polls "library" until the game is ready, then joins. The last few loaded games stay in memory, so going
back to one is instant. A client that connects gets the current game; sessions keep the game they started with.
"""
from __future__ import annotations

import asyncio
import logging
import time
from collections import OrderedDict
from pathlib import Path

from .catalog import Game, row, scan
from .recording import find, game_id, load
from .replay import ReplayData

log = logging.getLogger("library")

CATALOG_PER_PAGE = 10  # keeps a "catalog" reply datagram small (a row is ~250 bytes with its board)  # keeps a "catalog" reply datagram small
KEEP_LOADED = 3
RESCAN_AFTER_S = 5.0


class Library:
    def __init__(self, folder: Path, saved: Path | None = None, logs: Path | None = None):
        self.folder = folder
        self.saved = saved  # the player's saved games (the Replays page's Saved list), also loadable by id
        self.logs = logs  # games converted from Power.log (the Replays page's Logs list), also loadable by id
        self.games: list[Game] = []
        self.scanned_at = 0.0
        self.scanning = False
        self.current: ReplayData | None = None
        self.current_id: str | None = None
        self.state = "idle"  # idle | loading | ready | error
        self.error = ""
        self._requested: str | None = None
        self._loaded: OrderedDict[str, ReplayData] = OrderedDict()

    # ------------------------------------------------------------------ catalog

    async def rescan(self) -> None:
        if self.scanning:
            return
        self.scanning = True
        try:
            self.games = await asyncio.get_running_loop().run_in_executor(None, scan, self.folder)
            self.scanned_at = time.monotonic()
            log.info("catalog: %d game(s) in %s", len(self.games), self.folder)
        except Exception as e:  # a broken folder must not take the server down
            log.warning("catalog scan failed: %s", e)
        finally:
            self.scanning = False

    def catalog_page(self, start: int) -> str:
        """`catalog=<start>;total=<n>;scanning=<0|1>;` then one line per game:
        id|start|hero|place|turns|minutes|rating|change|board|hero powers|trinkets (rating/change empty when
        unknown; board is comma-separated card:attack:health:golden; hero powers and trinkets comma-separated)."""
        if start == 0 and not self.scanning and time.monotonic() - self.scanned_at > RESCAN_AFTER_S:
            asyncio.ensure_future(self.rescan())
        start = max(0, start)
        page = self.games[start:start + CATALOG_PER_PAGE]
        lines = [row(g) for g in page]
        head = f"catalog={start};total={len(self.games)};scanning={int(self.scanning)};"
        return head + "\n".join(lines)

    # ------------------------------------------------------------------ loading

    def status(self) -> str:
        """`library=<state>;id=<current or requested id>;` plus `error=<text>;` on error."""
        gid = self._requested or self.current_id or ""
        reply = f"library={self.state};id={gid};"
        if self.state == "error":
            reply += f"error={self.error.replace(';', ',')};"
        return reply

    def request(self, game_id: str) -> str:
        """Start loading game_id (no-op if it's current or already loading); returns the status."""
        if game_id == self.current_id and self.state == "ready":
            return self.status()
        if game_id == self._requested and self.state == "loading":
            return self.status()
        path = None
        if Path(game_id).name == game_id:
            for folder in (self.folder, self.saved, self.logs):
                path = find(folder, game_id) if folder else None
                if path:
                    break
        if path is None:
            self.state, self.error, self._requested = "error", f"no recording {game_id}", game_id
            return self.status()
        cached = self._loaded.get(game_id)
        if cached is not None:
            self._loaded.move_to_end(game_id)
            self._set_current(game_id, cached)
            return self.status()
        self._requested, self.state = game_id, "loading"
        asyncio.ensure_future(self._load(game_id, path))
        return self.status()

    def preload(self, path: Path) -> None:
        """Build path's replay now (blocking): the single-file mode's game."""
        data = ReplayData(load(path))
        self._loaded[game_id(path)] = data
        self._set_current(game_id(path), data)

    async def _load(self, game_id: str, path: Path) -> None:
        t = time.perf_counter()
        try:
            data = await asyncio.get_running_loop().run_in_executor(None, lambda: ReplayData(load(path)))
        except Exception as e:
            log.warning("loading %s failed: %s", game_id, e)
            if self._requested == game_id:
                self.state, self.error = "error", str(e)
            return
        log.info("loaded %s in %.1f s", game_id, time.perf_counter() - t)
        self._loaded[game_id] = data
        while len(self._loaded) > KEEP_LOADED:
            self._loaded.popitem(last=False)
        if self._requested == game_id:  # a later request wins
            self._set_current(game_id, data)

    def _set_current(self, game_id: str, data: ReplayData) -> None:
        self.current, self.current_id = data, game_id
        self._requested, self.state, self.error = None, "ready", ""
