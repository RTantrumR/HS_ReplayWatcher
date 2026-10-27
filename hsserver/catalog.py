"""Catalog of the replayable games in a recordings folder, for the in-game Replays page.

One entry per real Battlegrounds game: who was watched (hero), how it went (placement, rating change), when
and how long. Recordings of local replay sessions (the client watching this server) are skipped, as are
reconnect stubs and files without a game. Reading a game means decoding its PowerHistory (~2 s for a 35-min
match), so results are cached in ``<folder>/.catalog.json`` by file name, size and mtime. Rows in
``<folder>/.catalog.seed.txt`` (made-up games from tools/seed_catalog.py, for testing the page) are listed too.

    python -m hsserver.catalog [recordings/] [--keep N] [--saved <folder>] [--logs <folder>]
"""
from __future__ import annotations

import json
import logging
import os
import sys
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path

from .gamestate import GameState
from .proto import decode
from .recording import Recording, compress, game_id, load, recordings

log = logging.getLogger("catalog")

CACHE_NAME = ".catalog.json"
ROWS_NAME = ".catalog.txt"  # the same games as rows (see row()), read by the plugin's Replays page
SEED_NAME = ".catalog.seed.txt"  # made-up rows (tools/seed_catalog.py), listed with the real games
CACHE_VERSION = 10  # 4: spectated games left out of recordings (recording.watching_another); 5: place only when over;
# 6: the 2x2 grid of hero powers and trinkets (see icon_grid); 7: the final board's keywords; 8: the final hand;
# 9: a hero power traded away by a quest (traded_hero_power); 10: its quest's goal
HANDSHAKE, POWER_HISTORY, ENTITY_CHOICES, BG_RATING_CHANGE = 168, 19, 17, 34
TAG_TURN, TAG_HERO_ENTITY, TAG_PLAYER_LEADERBOARD_PLACE = 20, 27, 1373
TAG_PREMIUM, TAG_HEALTH, TAG_ATK, TAG_DAMAGE, TAG_ZONE, TAG_CONTROLLER, TAG_PLAYER_ID = 12, 45, 47, 44, 49, 50, 30
TAG_CARDTYPE, TAG_ZONE_POSITION, ZONE_PLAY, ZONE_HAND, CARDTYPE_MINION = 202, 263, 1, 3, 4
CARDTYPE_HERO_POWER, CARDTYPE_BATTLEGROUND_TRINKET = 10, 44
# A quest that trades a hero power away (see traded_hero_power); 1396 has no name in the client: a reward's quest.
TAG_LAST_AFFECTED_BY, TAG_QUEST, TAG_REWARD_QUEST, TAG_BACON_IS_HEROPOWER_QUESTREWARD = 18, 462, 1396, 2706
TAG_QUEST_PROGRESS_TOTAL = 535  # the {0} in a quest's text
# A minion's keywords as the stock Full Stats page shows them (BaconDisplay: taunt, divine shield, windfury, reborn,
# stealth, poisonous, or else venomous), one letter each in the row.
KEYWORDS = (("T", 190), ("D", 194), ("W", 189), ("R", 1085), ("S", 191), ("P", 363), ("V", 2853))
# The player's PLAYSTATE once their game is over (TAG_PLAYSTATE in the 36.6 client): WON, LOST, TIED, CONCEDED.
# PLAYER_LEADERBOARD_PLACE is the live leaderboard place from the first turn on, so it is final only then.
TAG_PLAYSTATE, PLAYSTATES_OVER = 17, {4, 5, 6, 8}
LOCAL_AURORA_PASSWORD = "local"  # what the plugin's local join sends (LocalServer.cs)
SETTLED_AFTER_S = 2 * 3600  # a recording untouched this long won't be reconnected to (PacketRecorder.ResumeWithinHours)


@dataclass
class Game:
    id: str  # the recording's file stem
    start: str  # "YYYY-MM-DD HH:MM" local time, from the file name
    hero: str  # card id of the watched player's hero
    place: int  # final placement (0 = unknown, e.g. the game was left before the end)
    turns: int  # shop turns played
    minutes: int
    rating: int | None  # rating after the game (BattlegroundsRatingChange), if it was sent
    change: int | None
    # final board: [card id, attack, health, golden 0/1, keyword letters (see KEYWORDS)] by position
    board: list[list] = field(default_factory=list)
    # The row's 2x2 grid of icons (see icon_grid): the top cells (hero powers, or trinkets standing in for one)
    # and the bottom cells (the first two trinkets picked).
    hero_powers: list[str] = field(default_factory=list)
    trinkets: list[str] = field(default_factory=list)
    hand: list[list] = field(default_factory=list)  # the hand the game ended with, as the board (spells: 0 attack/health)
    # The quest that traded the hero power away, its reward and the quest's goal (see traded_hero_power).
    hp_quest: list[str] = field(default_factory=list)


def is_local_session(rec: Recording) -> bool:
    c = rec.connect or {}
    return c.get("auroraPassword") == LOCAL_AURORA_PASSWORD or c.get("address") == "127.0.0.1"


def is_battlegrounds(rec: Recording) -> bool:
    return str((rec.connect or {}).get("nextGameType", "")).startswith("GT_BATTLEGROUNDS")


def _start_time(path: Path) -> str:
    try:
        return datetime.strptime(path.stem[:15], "%Y%m%d_%H%M%S").strftime("%Y-%m-%d %H:%M")
    except ValueError:
        return datetime.fromtimestamp(path.stat().st_mtime).strftime("%Y-%m-%d %H:%M")


def read_game(path: Path) -> Game | None:
    """The catalog entry for one recording, or None if it isn't a real BG game with a watched player."""
    rec = load(path)
    if is_local_session(rec) or not is_battlegrounds(rec):
        return None
    s2c = [p for p in rec.packets if p.dir == "S2C"]
    if not any(p.type == POWER_HISTORY for p in s2c):
        return None
    gs = GameState()
    player_entities: dict[int, int] = {}
    friendly_id = None
    turn = 0
    rating = change = None
    was_hero_power: set[int] = set()  # entities created or changed as a hero power (a trinket may replace one)
    for p in s2c:
        if p.type == POWER_HISTORY:
            ph = decode("PegasusGame.PowerHistory", p.body)
            for e in ph.get("List", []):
                if "CreateGame" in e:
                    player_entities = {pl.get("Id", 0): pl.get("Entity", {}).get("Id", 0)
                                       for pl in e["CreateGame"].get("Players", [])}
                for kind in ("FullEntity", "ShowEntity", "ChangeEntity"):
                    ent = e.get(kind)
                    if ent and any(t.get("Name") == TAG_CARDTYPE and t.get("Value") == CARDTYPE_HERO_POWER
                                   for t in ent.get("Tags", [])):
                        was_hero_power.add(ent.get("Entity", 0))
                tc = e.get("TagChange")
                if tc and tc.get("Entity") == gs.game_entity_id and tc.get("Tag") == TAG_TURN:
                    turn = tc.get("Value", 0)
            gs.apply_power_history(ph)
        elif p.type == ENTITY_CHOICES and friendly_id is None:
            # Choices go only to the player who chooses; the first one is the watched player's hero pick.
            friendly_id = decode("PegasusGame.EntityChoices", p.body).get("PlayerId")
        elif p.type == BG_RATING_CHANGE:
            rc = decode("PegasusGame.BattlegroundsRatingChange", p.body)
            rating, change = rc.get("NewRating"), rc.get("RatingChange")
    player = gs.entities.get(player_entities.get(friendly_id, -1))
    if player is None:
        return None
    hero = gs.entities.get(player.tags.get(TAG_HERO_ENTITY, -1))
    # The board the game ended with, as the client's own Full Stats shows it (verified on the real recording).
    pid = player.tags.get(TAG_PLAYER_ID)
    minions = [e for e in gs.entities.values()
               if e.tags.get(TAG_CARDTYPE) == CARDTYPE_MINION and e.tags.get(TAG_CONTROLLER) == pid
               and e.tags.get(TAG_ZONE) == ZONE_PLAY]
    minions.sort(key=lambda e: e.tags.get(TAG_ZONE_POSITION, 0))
    board = [card_entry(e) for e in minions[:7]]
    hand = sorted((e for e in gs.entities.values()
                   if e.tags.get(TAG_CONTROLLER) == pid and e.tags.get(TAG_ZONE) == ZONE_HAND and e.card_id),
                  key=lambda e: e.tags.get(TAG_ZONE_POSITION, 0))
    # Hero powers and trinkets in play at the end; entity ids follow creation order (the hero's own power first).
    in_play = sorted((e for e in gs.entities.values()
                      if e.tags.get(TAG_CONTROLLER) == pid and e.tags.get(TAG_ZONE) == ZONE_PLAY),
                     key=lambda e: e.id)
    hero_powers, trinkets = icon_grid(
        [e for e in in_play if e.tags.get(TAG_CARDTYPE) == CARDTYPE_HERO_POWER],
        [e for e in in_play if e.tags.get(TAG_CARDTYPE) == CARDTYPE_BATTLEGROUND_TRINKET], was_hero_power, path.name)
    hp_quest: list[str] = []
    if not hero_powers:
        hero_powers, hp_quest = traded_hero_power(gs, pid, was_hero_power)
    start_ms = next((p.ms for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE), rec.packets[0].ms)
    return Game(
        id=game_id(path),
        start=_start_time(path),
        hero=hero.card_id if hero else "",
        place=(hero.tags.get(TAG_PLAYER_LEADERBOARD_PLACE, 0)
               if hero and player.tags.get(TAG_PLAYSTATE) in PLAYSTATES_OVER else 0),
        turns=(turn + 1) // 2,
        minutes=round((rec.packets[-1].ms - start_ms) / 60000),
        rating=rating,
        change=change,
        board=board,
        hero_powers=hero_powers,
        trinkets=trinkets,
        hand=[card_entry(e) for e in hand[:10]],
        hp_quest=hp_quest,
    )


def traded_hero_power(gs: GameState, pid, was_hero_power: set[int]) -> tuple[list[str], list[str]]:
    """A game that ends with no hero power in play: the player's last one, and if a quest traded it away, that quest,
    its reward and the quest's goal ([quest, reward, QUEST_PROGRESS_TOTAL as text]; the reward and the goal when
    known). Sire Denathrius's "Whodunit?" (2026-10-27): it offers two quests,
    each with a reward pointing back at it (tag 1396, unnamed in the client) and marked as the hero power's
    (BACON_IS_HEROPOWER_QUESTREWARD); completing the chosen one (the hero power's LAST_AFFECTED_BY) removes the hero
    power, and its reward takes the slot."""
    gone = [gs.entities[i] for i in sorted(was_hero_power) if i in gs.entities
            and gs.entities[i].tags.get(TAG_CONTROLLER) == pid and gs.entities[i].card_id
            and gs.entities[i].tags.get(TAG_CARDTYPE) == CARDTYPE_HERO_POWER]
    if not gone:
        return [], []
    hp = gone[-1]
    quest = gs.entities.get(hp.tags.get(TAG_LAST_AFFECTED_BY, -1))
    if quest is None or not quest.tags.get(TAG_QUEST) or not quest.card_id:
        return [hp.card_id], []
    reward = next((e for e in gs.entities.values() if e.tags.get(TAG_REWARD_QUEST) == quest.id
                   and e.tags.get(TAG_BACON_IS_HEROPOWER_QUESTREWARD) and e.card_id), None)
    if reward is None:
        return [hp.card_id], [quest.card_id]
    total = quest.tags.get(TAG_QUEST_PROGRESS_TOTAL)
    return [hp.card_id], [quest.card_id, reward.card_id] + ([str(total)] if total else [])


def card_entry(e) -> list:
    """A board or hand card as the row keeps it: [card id, attack, health, golden 0/1, keyword letters]."""
    return [e.card_id, e.tags.get(TAG_ATK, 0), e.tags.get(TAG_HEALTH, 0) - e.tags.get(TAG_DAMAGE, 0),
            int(bool(e.tags.get(TAG_PREMIUM))), keywords(e.tags)]


def keywords(tags: dict) -> str:
    """The letters of a minion's keywords (KEYWORDS); venomous only without poisonous, as the stock page."""
    letters = "".join(k for k, tag in KEYWORDS if tags.get(tag))
    return letters.replace("V", "") if "P" in letters else letters


def icon_grid(hero_powers: list, trinkets: list, was_hero_power: set[int], name: str = "") -> tuple[list[str], list[str]]:
    """The row's 2x2 icon grid as (top cells, bottom cells) of card ids, from the hero powers and trinkets in play
    at the end (entities, in creation order). The top cells are the hero power ones: a trinket that replaced a hero
    power (Marin the Manager's, 2026-10-15: entity 121 changed from BG30_HERO_304p to a trinket) stays in its hero
    power's place, as the stock stats page shows its BACON_PAST_GAME_TRINKET_HEROPOWER in the hero power slot.
    The bottom cells are the first two trinkets picked; any later one (an anomaly's) takes a free top cell, where
    a second hero power would be. More than four icons can't all show: the last ones are left out."""
    standing_in = [t for t in trinkets if t.id in was_hero_power]
    picked = [t for t in trinkets if t.id not in was_hero_power]
    top = sorted(hero_powers + standing_in, key=lambda e: e.id) + picked[2:]
    if len(top) > 2:
        log.info("%s: %d icons, the grid shows 4; left out: %s", name, len(top) + len(picked[:2]),
                 ", ".join(e.card_id for e in top[2:]))
    return [e.card_id for e in top[:2]], [e.card_id for e in picked[:2]]


def row(g: Game) -> str:
    """id|start|hero|place|turns|minutes|rating|change|board|hero powers|trinkets|hand|hero power quest: rating/change
    empty when unknown; board and hand comma-separated card:attack:health:golden[:keywords]; hero powers and trinkets
    comma-separated: the icon grid's top and bottom cells (see icon_grid; a top cell may hold a trinket); the last
    field the quest and reward that traded the hero power away and the quest's goal (see traded_hero_power),
    comma-separated, or empty."""
    return (f"{g.id}|{g.start}|{g.hero}|{g.place}|{g.turns}|{g.minutes}|"
            f"{'' if g.rating is None else g.rating}|{'' if g.change is None else g.change}|"
            + _cards(g.board) + f"|{','.join(g.hero_powers)}|{','.join(g.trinkets)}|" + _cards(g.hand)
            + f"|{','.join(g.hp_quest)}")


def _cards(cards: list[list]) -> str:
    return ",".join(":".join(map(str, m[:4])) + (f":{m[4]}" if len(m) > 4 and m[4] else "") for m in cards)


def _parse_cards(field: str) -> list[list]:
    return [[p[0], int(p[1]), int(p[2]), int(p[3]), p[4] if len(p) > 4 else ""]
            for p in (m.split(":") for m in field.split(",") if m)]


def parse_row(line: str) -> Game | None:
    """The Game of a row() line, or None if it isn't one."""
    f = line.rstrip("\r\n").split("|")
    if len(f) < 11:
        return None
    try:
        return Game(id=f[0], start=f[1], hero=f[2], place=int(f[3]), turns=int(f[4]), minutes=int(f[5]),
                    rating=int(f[6]) if f[6] else None, change=int(f[7]) if f[7] else None, board=_parse_cards(f[8]),
                    hero_powers=[x for x in f[9].split(",") if x], trinkets=[x for x in f[10].split(",") if x],
                    hand=_parse_cards(f[11]) if len(f) > 11 else [],
                    hp_quest=[x for x in f[12].split(",") if x] if len(f) > 12 else [])
    except ValueError:
        return None


def _seeds(folder: Path) -> list[Game]:
    try:
        text = (folder / SEED_NAME).read_text(encoding="utf-8")
    except OSError:
        return []
    return [g for g in map(parse_row, text.splitlines()) if g]


def _read_cache(folder: Path) -> dict[str, dict]:
    """The folder's cache: file name -> {size, mtime, game} (empty when missing or of another version)."""
    try:
        cache = json.loads((folder / CACHE_NAME).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return cache.get("files", {}) if cache.get("version") == CACHE_VERSION else {}


def scan(folder: str | Path, keep: int | None = None, seed: dict[str, dict] | None = None) -> list[Game]:
    """Every replayable game in folder, newest first; reads only files the cache doesn't know as they are.
    A game's recording is compressed once the game is over for the player (a placement) or its file has been
    left alone long enough that no reconnect will carry it on. With keep, the recordings of all but the newest
    keep games are deleted (made-up seed rows aren't files and don't count). seed is another folder's cache
    (the recordings' one, for the saved folder): a file copied from there, same name, size and write time, is
    known without decoding."""
    folder = Path(folder)
    cache_path = folder / CACHE_NAME
    files = _read_cache(folder)
    fresh: dict[str, dict] = {}
    games: list[Game] = []
    paths: dict[str, Path] = {}
    for path in recordings(folder):
        st = path.stat()
        key = {"size": st.st_size, "mtime": st.st_mtime}
        known = files.get(path.name) or (seed or {}).get(path.name)
        if known and known.get("size") == key["size"] and known.get("mtime") == key["mtime"]:
            entry = known.get("game")
        else:
            t = time.perf_counter()
            try:
                game = read_game(path)
            except (ValueError, KeyError, IndexError) as e:
                log.warning("skipping %s: %s", path.name, e)
                game = None
            entry = asdict(game) if game else None
            if game:
                log.info("catalogued %s in %.1f s", path.name, time.perf_counter() - t)
        if entry and path.suffix == ".jsonl" and (entry["place"] > 0 or time.time() - st.st_mtime > SETTLED_AFTER_S):
            try:
                path = compress(path)
                st = path.stat()
                key = {"size": st.st_size, "mtime": st.st_mtime}
                log.info("compressed %s (%d KB)", path.name, st.st_size // 1024)
            except OSError as e:
                log.warning("could not compress %s: %s", path.name, e)
        fresh[path.name] = {**key, "game": entry}
        if entry:
            games.append(Game(**entry))
            paths[entry["id"]] = path
    if keep is not None and keep > 0:
        games.sort(key=lambda g: g.id, reverse=True)
        kept = games[:keep]
        for g in games[keep:]:
            try:
                paths[g.id].unlink()
                fresh.pop(paths[g.id].name, None)
                log.info("deleted %s (keeping the last %d games)", paths[g.id].name, keep)
            except OSError as e:
                log.warning("could not delete %s: %s", paths[g.id].name, e)
                kept.append(g)
        games = kept
    if fresh != files:
        try:
            cache_path.write_text(json.dumps({"version": CACHE_VERSION, "files": fresh}, indent=1), encoding="utf-8")
        except OSError as e:
            log.warning("could not write %s: %s", cache_path, e)
    games += _seeds(folder)
    games.sort(key=lambda g: g.id, reverse=True)
    _write_rows(folder / ROWS_NAME, games)
    return games


def _write_rows(path: Path, games: list[Game]) -> None:
    """The rows file, replaced whole (the plugin reads it at any moment, and writes it itself when a game is saved)."""
    text = "\n".join(row(g) for g in games)
    try:
        if not path.is_file() or path.read_text(encoding="utf-8") != text:
            tmp = path.with_name(path.name + ".tmp")
            tmp.write_text(text, encoding="utf-8", newline="\n")
            os.replace(tmp, path)
    except OSError as e:
        log.warning("could not write %s: %s", path, e)


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(message)s", datefmt="%H:%M:%S")
    import argparse
    ap = argparse.ArgumentParser(description="Catalog the replayable games of a recordings folder.")
    ap.add_argument("folder", nargs="?", default="recordings")
    ap.add_argument("--keep", type=int, help="delete the recordings of all but the newest KEEP games")
    ap.add_argument("--saved", help="also catalog this folder of saved games (its own catalog files; nothing deleted)")
    ap.add_argument("--logs", help="also catalog this folder of games converted from Power.log (python -m hsserver.powerlog; "
                                   "its own catalog files, nothing deleted)")
    args = ap.parse_args()
    for g in scan(args.folder, args.keep):
        print(g)
    if args.saved and Path(args.saved).is_dir():
        for g in scan(args.saved, seed=_read_cache(Path(args.folder))):
            print("saved:", g)
    if args.logs and Path(args.logs).is_dir():
        for g in scan(args.logs):
            print("logs:", g)


if __name__ == "__main__":
    main()
