"""Hearthstone's Power.log turned into a recording the replay server plays.

A game played without the plugin leaves only the client's text log (`<HS>\\Logs\\Hearthstone_<date>\\Power.log`;
older clients: `Logs\\Power.log` + rotated `Power_old*.log`). GameState.DebugPrintPowerList/DebugPrintPower
print every PowerHistory packet as it arrives, entry by entry, so the packet stream can be rebuilt almost
whole; DebugPrintEntityChoices/EntitiesChosen/Options print the choice and option packets. What the log lacks
is rebuilt where it can be (the player's clicks, from the blocks that answered them) and left out where it
can't (turn timer, race counts, round history, rating, DefTags, EffectCardId). The client's second print of
each entry, PowerTaskList.DebugPrintPower when it *executes* it, times the combat notes the recorder would
have written (`combat_done`, `block ...`), so a log replay's combats are timed like a recorded one's.

Names (tags, enum values, block and meta types, errors) come from hsserver/enums.json (tools/gen_enums.py).

    python -m hsserver.powerlog <Power.log | Power_old.log | folder>... [--out DIR] [--date YYYY-MM-DD] [--force]

The output is `<out>\\<yyyyMMdd_HHmmss>_log<GAME_SEED>.jsonl.gz`, an ordinary recording (recording.py's line
format) whose connect line says `"source": "powerlog"`. See docs/powerlog.md.
"""
from __future__ import annotations

import argparse
import base64
import copy
import datetime as dt
import gzip
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

from .gamestate import GameState
from .proto import encode

ENUMS_PATH = Path(__file__).with_name("enums.json")

# Packet types (hsserver/packets.py).
CHOOSE_OPTION, CHOOSE_ENTITIES, TURN_TIMER, ENTITIES_CHOSEN, ALL_OPTIONS, GAME_SETUP, ENTITY_CHOICES = 2, 3, 9, 13, 14, 16, 17
POWER_HISTORY, GAME_GUARDIAN_VARS, HANDSHAKE = 19, 35, 168
PACKET_MSG = {
    CHOOSE_OPTION: "PegasusGame.ChooseOption", CHOOSE_ENTITIES: "PegasusGame.ChooseEntities",
    ENTITIES_CHOSEN: "PegasusGame.EntitiesChosen", ALL_OPTIONS: "PegasusGame.AllOptions",
    GAME_SETUP: "PegasusGame.GameSetup", ENTITY_CHOICES: "PegasusGame.EntityChoices",
    POWER_HISTORY: "PegasusGame.PowerHistory", GAME_GUARDIAN_VARS: "PegasusGame.GameGuardianVars",
    HANDSHAKE: "PegasusGame.Handshake", TURN_TIMER: "PegasusGame.TurnTimer",
}
# Tags (GAME_TAG).
TAG_STEP, TAG_TURN, TAG_ZONE, TAG_PLAYER_ID, TAG_STATE, TAG_GAME_SEED, TAG_LEADERBOARD_PLACE, TAG_BACON_IN_COMBAT_PHASE = 19, 20, 49, 30, 204, 2042, 1373, 1522
ZONE_REMOVEDFROMGAME = 5
TAG_CARDTYPE, CARDTYPE_MINION = 202, 4
# A shop turn's timer runs out this long before the next combat starts (49 timers in 3 recordings: 2.5-3.9 s).
TIMER_BEFORE_COMBAT_S = 3
# The turn's last timer, Seconds 0, comes this long before the combat starts (median of 4 recordings).
ZERO_TIMER_BEFORE_COMBAT_MS = 400
STEP_MAIN_END, STATE_COMPLETE = 12, 3
CHOICE_MULLIGAN = 1  # CHOICE_TYPE
BLOCK_ATTACK, BLOCK_TRIGGER, BLOCK_DEATHS, BLOCK_PLAY, BLOCK_GAME_RESET = 1, 5, 6, 7, 11  # HistoryBlock.Type
OPTION_END_TURN = 2

# What a real BG game's server sent (recording 20261013_191943_game14353): the log has neither packet.
GAME_SETUP_BODY = {"Board": 71, "MaxSecretZoneSizePerPlayer": 5, "MaxSecretsPerPlayer": 5, "MaxQuestsPerPlayer": 5,
                   "MaxFriendlyMinionsPerPlayer": 7, "KeepAliveFrequencySeconds": 15, "DisconnectWhenStuckSeconds": 25,
                   "KeepAliveRetry": 1, "KeepAliveWaitForInternetSeconds": 20, "BoardLayout": 0,
                   "BaconFavoriteBoardSkin": 1}
GUARDIAN_VARS_BODY = {"ClientLostFrameTimeCatchUpThreshold": 4.0, "ClientLostFrameTimeCatchUpUseSlush": False,
                      "ClientLostFrameTimeCatchUpLowEndOnly": False, "GameAllowDeferredPowers": True,
                      "GameAllowBatchedPowers": True, "GameAllowDiamondCards": True, "BattlegroundAllowBuddies": True,
                      "BattlegroundsAllowQuestRewards": True, "BattlegroundAllowAnomalies": True,
                      "BattlegroundAllowTrinkets": True, "BattlegroundAllowAltTavern": True,
                      "SendStuckGameBecauseOfSpellTelemetry": True, "BattlegroundAllowDarkGifts": True}
OPENING_MS = 2000  # the game's first log line lands here; the handshake and setup packets come before it
RECONNECT_WINDOW_MS = 3 * 3600 * 1000  # a game cut off in one log file goes on in a later one within this (as the recorder)
SAME_GAME_MS = RECONNECT_WINDOW_MS + 2 * 3600 * 1000  # one seed's starts this close are one game (its parts)
EPOCH = dt.datetime(1970, 1, 1)  # times are ms since this, local time (the log's clock)
CLICK_LEAD_MS = 300  # an inferred click goes this long before the packet that answered it (median measured: 274)

LINE_RE = re.compile(r"^([DWIE]) (\d\d):(\d\d):(\d\d)\.(\d{7}) ([\w.]+)\(\) - (.*)$")
ID_IN_BRACKETS_RE = re.compile(r"\bid=(\d+)\b")
TAG_RE = re.compile(r"^tag=(\w+) value=(-?\w+)\s*$")
TAG_CHANGE_RE = re.compile(r"^TAG_CHANGE Entity=(.*) tag=(\w+) value=(-?\w+)( DEF CHANGE)?\s*$")
FULL_ENTITY_RE = re.compile(r"^FULL_ENTITY - (?:Creating ID=(\d+)|Updating (.*)) CardID=(\S*)\s*$")
SHOW_ENTITY_RE = re.compile(r"^(SHOW|CHANGE)_ENTITY - Updating Entity=(.*) CardID=(\S*)\s*$")
HIDE_ENTITY_RE = re.compile(r"^HIDE_ENTITY - Entity=(.*) tag=(\w+) value=(-?\w+)\s*$")
BLOCK_START_RE = re.compile(r"^BLOCK_START BlockType=(\w+) Entity=(.*?) EffectCardId=(.*?) EffectIndex=(-?\d+) "
                            r"Target=(.*?) SubOption=(-?\d+)(?: TriggerKeyword=(\w+))?\s*$")
META_DATA_RE = re.compile(r"^META_DATA - Meta=(\w+) Data=(.*) InfoCount=(\d+)\s*$")
INFO_RE = re.compile(r"^Info\[(\d+)\] = (.*)$")
SUB_SPELL_RE = re.compile(r"^SUB_SPELL_START - SpellPrefabGUID=(\S*) Source=(\d+) TargetCount=(\d+)\s*$")
SUB_SPELL_TARGET_RE = re.compile(r"^(Source|Targets\[\d+\]) = (.*)$")
VO_SPELL_RE = re.compile(r"^VO_SPELL - BrassRingGuid=(\S*) - VoSpellPrefabGUID=(\S*) - Blocking=(\w+) - AdditionalDelayInMs=(-?\d+)")
SHUFFLE_RE = re.compile(r"^SHUFFLE_DECK PlayerID=(\d+)")
CACHED_TAG_RE = re.compile(r"^CACHED_TAG_FOR_DORMANT_CHANGE Entity=(.*) tag=(\w+) value=(-?\w+)\s*$")
GAME_ENTITY_RE = re.compile(r"^GameEntity EntityID=(\d+)")
PLAYER_RE = re.compile(r"^Player EntityID=(\d+) PlayerID=(\d+) GameAccountId=\[hi=(\d+) lo=(\d+)\]")
CHOICES_RE = re.compile(r"^id=(\d+) Player=(.*) TaskList=(\S*) ChoiceType=(\w+) CountMin=(\d+) CountMax=(\d+)\s*$")
CHOSEN_RE = re.compile(r"^id=(\d+) Player=(.*) EntitiesCount=(\d+)\s*$")
ENTITIES_RE = re.compile(r"^Entities\[(\d+)\]=(.*)$")
OPTION_RE = re.compile(r"^option (\d+) type=(\w+) mainEntity=(.*) error=(\w+) errorParam=(-?\d*)\s*$")
TARGET_RE = re.compile(r"^(target|subOption) (\d+) entity=(.*) error=(\w+) errorParam=(-?\d*)\s*$")
HEADER_PLAYER_RE = re.compile(r"^PlayerID=(\d+), PlayerName=(.*)$")
SEND_OPTION_RE = re.compile(r"^selectedOption=(-?\d+) selectedSubOption=(-?\d+) selectedTarget=(-?\d+) selectedPosition=(-?\d+)")
SEND_CHOICES_RE = re.compile(r"^id=(\d+) ChoiceType=(\w+)")
CHOSEN_ENTITY_RE = re.compile(r"^m_chosenEntities\[(\d+)\]=(.*)$")
SESSION_DIR_RE = re.compile(r"Hearthstone_(\d{4})_(\d\d)_(\d\d)_\d\d_\d\d_\d\d")
CONTINUATIONS = ("tag=", "Info[", "Source =", "Targets[", "GameEntity EntityID=", "Player EntityID=")


def split_entities(text: str) -> list[str]:
    """A BLOCK_START's Entity= list (GetEntitiesLogNames joins names with ","; a batched block has several)
    split at the commas between entries, not the ones inside a bracketed name ("[entityName=Inge, the Iron
    Hymn id=119 ...]")."""
    parts, depth, cur = [], 0, []
    for ch in text:
        if ch == "[":
            depth += 1
        elif ch == "]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p for p in parts if p.strip()]


class LogError(Exception):
    """The log says something this reader doesn't understand; the message names the file and line."""


class Enums:
    """The client's enum tables (enums.json): names the log prints -> the numbers the packets carry."""

    def __init__(self, path: Path = ENUMS_PATH) -> None:
        data = json.loads(path.read_text(encoding="utf-8"))
        self.build: int = data["build"]
        self.enums: dict[str, dict[str, int]] = data["enums"]
        self.value_enums: dict[str, str] = data["value_enums"]
        # Names no table has, with how often: an older client's names since renamed or removed (an old log),
        # or a newer client's (regenerate enums.json). A tag with no number is dropped; a value becomes 0.
        self.unknown: dict[str, int] = {}

    def _lookup(self, table: str, name: str) -> int | None:
        if re.fullmatch(r"-?\d+", name):
            return int(name)
        v = self.enums[table].get(name)
        if v is None:
            key = f"{table}.{name}"
            self.unknown[key] = self.unknown.get(key, 0) + 1
        return v

    def tag(self, name: str) -> int | None:
        return self._lookup("GAME_TAG", name)

    def value(self, tag_name: str, value_name: str) -> int:
        if re.fullmatch(r"-?\d+", value_name):
            return int(value_name)
        table = self.value_enums.get(tag_name)
        if table is None:
            key = f"value {tag_name}={value_name}"
            self.unknown[key] = self.unknown.get(key, 0) + 1
            return 0
        return self._lookup(table, value_name) or 0

    def tag_msg(self, tag_name: str, value_name: str) -> dict | None:
        """A {Name, Value} tag, or None for a tag with no number."""
        tag = self.tag(tag_name)
        return None if tag is None else {"Name": tag, "Value": self.value(tag_name, value_name)}

    def block_type(self, name: str) -> int:
        return self._lookup("HistoryBlock.Type", name) or 0

    def meta_type(self, name: str) -> int:
        return self._lookup("HistoryMeta.Type", name) or 0

    def play_error(self, name: str) -> int:
        return self._lookup("PlayErrors.ErrorType", name) or 0

    def choice_type(self, name: str) -> int:
        return self._lookup("CHOICE_TYPE", name) or 0


@dataclass
class Line:
    ms: int  # since the first line read, day wraps folded in
    logger: str
    text: str
    where: str  # "file:line" for messages


def read_lines(path: Path):
    """A log's lines in order, timed from midnight of the day it starts (the log has times of day only: a step
    back of more than 12 h is the next day). Lines without the logger prefix (exception text, banners) are dropped."""
    last_day_ms = None
    days = 0
    with path.open(encoding="utf-8", errors="replace") as f:
        for no, raw in enumerate(f, 1):
            m = LINE_RE.match(raw.rstrip("\r\n"))
            if not m:
                continue
            day_ms = ((int(m.group(2)) * 60 + int(m.group(3))) * 60 + int(m.group(4))) * 1000 + int(m.group(5)) // 10000
            if last_day_ms is not None and day_ms < last_day_ms - 12 * 3600 * 1000:
                days += 1
            last_day_ms = day_ms
            yield Line(days * 86400000 + day_ms, m.group(6), m.group(7), f"{path.name}:{no}")


def first_day(path: Path, last_ms: int, override: dt.date | None) -> dt.date:
    """The date of a log's first line. Given, else read off the session folder (Hearthstone_YYYY_MM_DD_..., the
    client's start), else worked back from the file's modification time, which is its last line's."""
    if override:
        return override
    for part in reversed(path.resolve().parts):
        m = SESSION_DIR_RE.search(part)
        if m:
            return dt.date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
    mtime = dt.datetime.fromtimestamp(path.stat().st_mtime)
    mtime_ms = ((mtime.hour * 60 + mtime.minute) * 60 + mtime.second) * 1000
    last_date = mtime.date() if last_ms % 86400000 <= mtime_ms + 600000 else mtime.date() - dt.timedelta(days=1)
    return last_date - dt.timedelta(days=last_ms // 86400000)


@dataclass
class Packet:
    ms: int
    dir: str
    type: int
    body: dict
    inferred: bool = False  # a click the converter worked out (dropped when the log has the player's own)


@dataclass
class Game:
    """One game as the log shows it: its packets (bodies as dicts, encoded when written) and recorder notes."""
    start_ms: int  # the CREATE_GAME packet's line time (read_lines' clock; ms since EPOCH once the file is done)
    where: str
    file: str = ""  # the log it came from
    packets: list[Packet] = field(default_factory=list)
    notes: list[tuple[int, str]] = field(default_factory=list)
    header: dict = field(default_factory=dict)  # DebugPrintGame: BuildNumber, GameType, FormatType, ScenarioID
    names: dict[int, str] = field(default_factory=dict)  # DebugPrintGame: player id -> name
    seed: int = 0
    game_entity: int = 0
    players: dict[int, int] = field(default_factory=dict)  # player id -> player entity id
    local_player: int = 0  # player id of the player the log was written for (the one with a real account id)
    complete: bool = False
    end_ms: int = 0
    reconnects: list[int] = field(default_factory=list)  # ms of each CREATE_GAME that continued this game
    clicks_inferred: int = 0
    clicks_sent: int = 0  # the player's own clicks the log printed (GameState.SendOption / SendChoices)
    picks: list[tuple[dict, int]] = field(default_factory=list)  # (EntityChoices body, the chosen entity)
    combat_starts: list[int] = field(default_factory=list)  # ms of each packet that set BACON_IN_COMBAT_PHASE to 1
    # (ms, TURN) of each turn's first AllOptions, or EntityChoices when the turn opens with a choice
    turn_decisions: list[tuple[int, int]] = field(default_factory=list)
    counts: dict[str, int] = field(default_factory=dict)

    @property
    def is_battlegrounds(self) -> bool:
        return str(self.header.get("GameType", "")).startswith("GT_BATTLEGROUNDS")

    def local_entity(self) -> int:
        return self.players.get(self.local_player, 0)


class Parser:
    """Feeds lines, collects games. One instance per ordered list of files (a game may span a rotation)."""

    def __init__(self, enums: Enums, strict: bool = False) -> None:
        self.enums = enums
        self.strict = strict  # a name enums.json doesn't know fails the conversion instead of being dropped
        self.games: list[Game] = []
        self.game: Game | None = None
        self.state = GameState()  # the stream as received (controllers for click inference, ResetGame rebuilds)
        self.executed = GameState()  # the stream as executed (the PowerTaskList pass): combat phase for notes
        # The GameState-pass packet being read.
        self._entries: list[dict] | None = None
        self._count = 0
        self._packet_ms = 0
        self._entry: dict | None = None  # the entry taking continuation lines
        self._entry_kind = ""
        self._cg_part: dict | None = None  # CREATE_GAME: the GameEntity/Player whose tag lines follow
        self._cg_where = ""
        # Other GameState prints being read: ("choices"|"chosen"|"options", dict, ms).
        self._print: tuple[str, dict, int] | None = None
        # The PowerTaskList pass.
        self._ptl_phase = 0  # the executed BACON_IN_COMBAT_PHASE (TAG_CHANGEs, and a CREATE_GAME's game entity tags)
        self._ptl_in_game_entity = False  # inside a PowerTaskList-pass CREATE_GAME's GameEntity tag lines
        # Open AllOptions awaiting the click that answered it: (id, option ids -> index, ms, option entity ids).
        self._open_options: tuple[dict, int] | None = None  # the last AllOptions not yet answered, and its time
        self._answered: set[int] = set()  # option ids a click was inferred for (a reprint doesn't reopen them)
        self._last_options_id = 0  # the last AllOptions printed: what a SendOption answers
        self.partial_lines = 0  # lines before a file's first CREATE_GAME (a game the previous file began)
        # The combat phase tag as the log may print it: by name, or by number for a client that had no name for it.
        self._phase_names = {str(TAG_BACON_IN_COMBAT_PHASE)} | {
            n for n, v in enums.enums["GAME_TAG"].items() if v == TAG_BACON_IN_COMBAT_PHASE}
        self._file = ""

    def start_file(self, name: str) -> None:
        """A new log file: its clock is its own, and a game the last one cut off is only joined up afterwards
        (join_reconnects), so nothing carries over."""
        self._flush_packet(None)
        self._flush_print()
        self.game = None
        self.executed = GameState()
        self._ptl_phase = 0
        self._open_options = None
        self._file = name

    def end_file(self, offset_ms: int, first_game: int) -> None:
        """The file is read: put its games (games[first_game:]) on the absolute clock."""
        self._flush_packet(None)
        self._flush_print()
        for g in self.games[first_game:]:
            g.start_ms += offset_ms
            g.end_ms += offset_ms
            g.reconnects = [ms + offset_ms for ms in g.reconnects]
            for p in g.packets:
                p.ms += offset_ms
            g.notes = [(ms + offset_ms, text) for ms, text in g.notes]
            g.combat_starts = [ms + offset_ms for ms in g.combat_starts]
            g.turn_decisions = [(ms + offset_ms, turn) for ms, turn in g.turn_decisions]

    # ---- driving ------------------------------------------------------------------------------------------

    def feed(self, line: Line) -> None:
        logger, text = line.logger, line.text
        if logger == "GameState.DebugPrintPower":
            self._power_line(line)
            return
        self._flush_packet(line)
        if logger == "GameState.DebugPrintPowerList":
            m = re.match(r"^Count=(\d+)", text)
            if not m:
                raise LogError(f"{line.where}: unreadable {text!r}")
            self._entries, self._count, self._packet_ms, self._entry = [], int(m.group(1)), line.ms, None
            return
        if self.game is None:
            if logger.startswith("GameState."):
                self.partial_lines += 1
            return
        if logger == "GameState.DebugPrintGame":
            self._header_line(text)
        elif logger in ("GameState.DebugPrintEntityChoices", "GameState.DebugPrintEntitiesChosen", "GameState.DebugPrintOptions"):
            self._print_line(logger, line)
        elif logger in ("GameState.SendOption", "GameState.SendChoices"):
            self._sent_line(logger, line)
        elif logger == "PowerTaskList.DebugPrintPower":
            self._executed_line(line)

    def finish(self) -> list[Game]:
        self._flush_packet(None)
        self._flush_print()
        if self.enums.unknown and self.strict:
            raise LogError("names enums.json doesn't know: " + ", ".join(sorted(self.enums.unknown)))
        return self.games

    # ---- the GameState pass: packets ----------------------------------------------------------------------

    def _power_line(self, line: Line) -> None:
        self._flush_print()
        stripped = line.text.lstrip(" ")
        if self._entries is None:
            raise LogError(f"{line.where}: power line outside a packet")
        if stripped.startswith(CONTINUATIONS):
            self._continuation(stripped, line)
            return
        entry = self._parse_entry(stripped, line)
        self._entries.append(entry)  # a dropped entry (an unknown tag) still counts towards Count
        if not stripped.startswith("CREATE_GAME"):
            self._cg_part = None

    def _flush_packet(self, line: Line | None) -> None:
        if self._entries is None:
            return
        entries, count, ms = self._entries, self._count, self._packet_ms
        self._entries = self._entry = self._cg_part = None
        if len(entries) != count:
            raise LogError(f"{line.where if line else 'end'}: packet of {ms} ms printed Count={count} but {len(entries)} entries were read")
        entries = [e for e in entries if "_dropped" not in e]
        if not entries:
            return
        self._packet(entries, ms)

    def _packet(self, entries: list[dict], ms: int) -> None:
        if "CreateGame" in entries[0]:
            self._create_game(entries[0]["CreateGame"], ms)
        elif self.game is None:
            self.partial_lines += len(entries)
            return
        game = self.game
        assert game is not None
        for e in entries:
            if "ResetGame" in e:
                e["ResetGame"]["CreateGame"] = self.state.current_create_game()
        self._infer_click(entries, ms)
        game.packets.append(Packet(ms, "S2C", POWER_HISTORY, {"List": entries}))
        ge = self.state.entities.get(game.game_entity)
        in_combat = ge is not None and ge.tags.get(TAG_BACON_IN_COMBAT_PHASE) == 1
        self.state.apply_power_history({"List": entries})
        ge = self.state.entities.get(game.game_entity)
        if not in_combat and ge is not None and ge.tags.get(TAG_BACON_IN_COMBAT_PHASE) == 1:
            game.combat_starts.append(ms)
        game.end_ms = max(game.end_ms, ms)
        ge = self.state.entities.get(game.game_entity)
        if ge is not None and ge.tags.get(TAG_STATE) == STATE_COMPLETE:
            game.complete = True
        for e in entries:
            if "PowerStart" in e:
                game.counts["blocks"] = game.counts.get("blocks", 0) + 1

    def _create_game(self, cg: dict, ms: int) -> None:
        seed = next((t["Value"] for t in cg["GameEntity"]["Tags"] if t["Name"] == TAG_GAME_SEED), 0)
        if self.game is not None and not self.game.complete and seed and seed == self.game.seed:
            self.game.reconnects.append(ms)  # the same game again: the client reconnected
        else:
            self.game = Game(ms, self._cg_where, file=self._file, seed=seed)
            self._answered = set()
            self.games.append(self.game)
            self.executed = GameState()
            self._ptl_phase = 0
        game = self.game
        game.game_entity = cg["GameEntity"]["Id"]
        game.players = {p["Id"]: p["Entity"]["Id"] for p in cg["Players"]}
        real = [p["Id"] for p in cg["Players"] if p["GameAccountId"]["Hi"] or p["GameAccountId"]["Lo"]]
        game.local_player = real[0] if len(real) == 1 else 0
        self._open_options = None

    def _parse_entry(self, s: str, line: Line) -> dict:
        E = self.enums
        if s.startswith("TAG_CHANGE "):
            m = TAG_CHANGE_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry = None
            tag = E.tag_msg(m.group(2), m.group(3))
            if tag is None:
                return {"_dropped": s}
            tc = {"Entity": self._entity(m.group(1), line), "Tag": tag["Name"], "Value": tag["Value"]}
            if m.group(4):
                tc["ChangeDef"] = True
            return {"TagChange": tc}
        if s.startswith("BLOCK_START "):
            m = BLOCK_START_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            btype = E.block_type(m.group(1))
            source = self._entity(split_entities(m.group(2))[0], line)
            ps = {"Type": btype, "SubOption": int(m.group(6)), "Source": source, "Target": self._entity(m.group(5), line)}
            if btype not in (BLOCK_DEATHS, BLOCK_PLAY, BLOCK_GAME_RESET):  # the server sends these four fields only
                ps["EffectIndex"] = int(m.group(4))
                if btype == BLOCK_TRIGGER:
                    ps["TriggerKeyword"] = E.tag(m.group(7) or "0") or 0
                ps.update(ShowInHistory=False, IsDeferrable=False, IsBatchable=False, IsDeferBlocker=False)
            self._entry = None
            return {"PowerStart": ps}
        if s.startswith("BLOCK_END"):
            self._entry = None
            return {"PowerEnd": {}}
        if s.startswith("FULL_ENTITY "):
            m = FULL_ENTITY_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            eid = int(m.group(1)) if m.group(1) else self._entity(m.group(2), line)
            self._entry, self._entry_kind = {"Entity": eid, "Name": m.group(3), "Tags": []}, "tags"
            return {"FullEntity": self._entry}
        if s.startswith(("SHOW_ENTITY ", "CHANGE_ENTITY ")):
            m = SHOW_ENTITY_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry, self._entry_kind = {"Entity": self._entity(m.group(2), line), "Name": m.group(3), "Tags": []}, "tags"
            return {("ShowEntity" if m.group(1) == "SHOW" else "ChangeEntity"): self._entry}
        if s.startswith("HIDE_ENTITY "):
            m = HIDE_ENTITY_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry = None
            return {"HideEntity": {"Entity": self._entity(m.group(1), line), "Zone": E.value("ZONE", m.group(3))}}
        if s.startswith("META_DATA "):
            m = META_DATA_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            md: dict = {"Info": []}
            mtype = E.meta_type(m.group(1))
            if mtype:
                md["Type"] = mtype
            if re.fullmatch(r"-?\d+", m.group(2)) and int(m.group(2)):
                md["Data"] = int(m.group(2))
            self._entry, self._entry_kind = md, "info"
            return {"MetaData": md}
        if s.startswith("SUB_SPELL_START "):
            m = SUB_SPELL_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            ss = {"SpellPrefabGuid": m.group(1), "SourceEntityId": int(m.group(2)), "TargetEntityIds": []}
            self._entry, self._entry_kind = ss, "targets"
            return {"SubSpellStart": ss}
        if s.startswith("SUB_SPELL_END"):
            self._entry = None
            return {"SubSpellEnd": {}}
        if s.startswith("CREATE_GAME"):
            self._entry, self._cg_part, self._cg_where = None, None, line.where
            self._entry = {"GameEntity": {"Id": 0, "Tags": []}, "Players": []}
            self._entry_kind = "create"
            return {"CreateGame": self._entry}
        if s.startswith("RESET_GAME"):
            self._entry = None
            return {"ResetGame": {"SourceIsRewind": False}}  # its CreateGame is rebuilt when the packet closes
        if s.startswith("VO_SPELL "):
            m = VO_SPELL_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry = None  # the printer swaps the two guids' labels (GameState.DebugPrintPower)
            return {"VoSpell": {"SpellPrefabGuid": m.group(1), "BrassRingPrefabGuid": m.group(2),
                                "Blocking": m.group(3) == "True", "AdditionalDelayMs": int(m.group(4))}}
        if s.startswith("SHUFFLE_DECK "):
            m = SHUFFLE_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry = None
            return {"ShuffleDeck": {"PlayerId": int(m.group(1))}}
        if s.startswith("CACHED_TAG_FOR_DORMANT_CHANGE "):
            m = CACHED_TAG_RE.match(s)
            if not m:
                raise LogError(f"{line.where}: unreadable {s!r}")
            self._entry = None
            tag = E.tag_msg(m.group(2), m.group(3))
            if tag is None:
                return {"_dropped": s}
            return {"CachedTagForDormantChange": {"Entity": self._entity(m.group(1), line), **tag}}
        raise LogError(f"{line.where}: unknown power line {s!r}")

    def _continuation(self, s: str, line: Line) -> None:
        if self._entry is None:
            raise LogError(f"{line.where}: continuation {s!r} with no entry open")
        E = self.enums
        if self._entry_kind == "create":
            m = GAME_ENTITY_RE.match(s)
            if m:
                self._entry["GameEntity"]["Id"] = int(m.group(1))
                self._cg_part = self._entry["GameEntity"]
                return
            m = PLAYER_RE.match(s)
            if m:
                player = {"Id": int(m.group(2)), "GameAccountId": {"Hi": int(m.group(3)), "Lo": int(m.group(4))},
                          "CardBack": 0, "Entity": {"Id": int(m.group(1)), "Tags": []}}
                self._entry["Players"].append(player)
                self._cg_part = player["Entity"]
                return
            m = TAG_RE.match(s)
            if m and self._cg_part is not None:
                tag = E.tag_msg(m.group(1), m.group(2))
                if tag is not None:
                    self._cg_part["Tags"].append(tag)
                return
        elif self._entry_kind == "tags":
            m = TAG_RE.match(s)
            if m:
                tag = E.tag_msg(m.group(1), m.group(2))
                if tag is not None:
                    self._entry["Tags"].append(tag)
                return
        elif self._entry_kind == "info":
            m = INFO_RE.match(s)
            if m:
                self._entry["Info"].append(self._entity(m.group(2), line))
                return
        elif self._entry_kind == "targets":
            m = SUB_SPELL_TARGET_RE.match(s)
            if m:
                if m.group(1) != "Source":
                    self._entry["TargetEntityIds"].append(self._entity(m.group(2), line))
                return
        raise LogError(f"{line.where}: continuation {s!r} doesn't fit a {self._entry_kind} entry")

    def _entity(self, text: str, line: Line) -> int:
        """An entity reference as GetEntityLogName prints it: a bare id, GameEntity, a player's name, or a
        bracketed description carrying id=N."""
        text = text.strip()
        if re.fullmatch(r"-?\d+", text):
            return int(text)
        m = ID_IN_BRACKETS_RE.search(text)
        if m:
            return int(m.group(1))
        if text == "GameEntity":
            return self.game.game_entity if self.game else 0
        game = self.game
        if game is None:
            raise LogError(f"{line.where}: entity {text!r} before any game")
        for pid, name in game.names.items():
            if name == text and pid in game.players:
                return game.players[pid]
        if text == "UNKNOWN HUMAN PLAYER" and game.local_entity():
            return game.local_entity()
        # Battlegrounds: the other player entity is Bob's, renamed to whichever opponent is up.
        others = [e for pid, e in game.players.items() if pid != game.local_player]
        if len(others) == 1 and game.local_entity():
            game.counts["opponent names"] = game.counts.get("opponent names", 0) + 1
            return others[0]
        raise LogError(f"{line.where}: can't tell which entity {text!r} is")

    # ---- the GameState pass: header, choices, options -----------------------------------------------------

    def _header_line(self, text: str) -> None:
        game = self.game
        assert game is not None
        m = HEADER_PLAYER_RE.match(text)
        if m:
            game.names[int(m.group(1))] = m.group(2)
            return
        key, _, value = text.partition("=")
        game.header[key] = int(value) if value.isdigit() else value

    def _print_line(self, logger: str, line: Line) -> None:
        text = line.text
        E = self.enums
        if logger == "GameState.DebugPrintEntityChoices":
            m = CHOICES_RE.match(text)
            if m:
                self._flush_print()
                self._print = ("choices", {"Id": int(m.group(1)), "ChoiceType": E.choice_type(m.group(4)),
                                           "CountMin": int(m.group(5)), "CountMax": int(m.group(6)), "Entities": [],
                                           "Source": 0, "PlayerId": self._player_id(m.group(2), line),
                                           "HideChosen": False}, line.ms)
                return
            s = text.strip()
            if s.startswith("Source=") and self._print and self._print[0] == "choices":
                self._print[1]["Source"] = self._entity(s[len("Source="):], line)
                return
            m = ENTITIES_RE.match(s)
            if m and self._print and self._print[0] == "choices":
                self._print[1]["Entities"].append(self._entity(m.group(2), line))
                return
        elif logger == "GameState.DebugPrintEntitiesChosen":
            m = CHOSEN_RE.match(text)
            if m:
                self._flush_print()
                self._print = ("chosen", {"ChooseEntities": {"Id": int(m.group(1)), "Entities": []},
                                          "PlayerId": self._player_id(m.group(2), line), "ChoiceType": 0}, line.ms)
                return
            m = ENTITIES_RE.match(text.strip())
            if m and self._print and self._print[0] == "chosen":
                self._print[1]["ChooseEntities"]["Entities"].append(self._entity(m.group(2), line))
                return
        elif logger == "GameState.DebugPrintOptions":
            m = re.match(r"^id=(\d+)\s*$", text)
            if m:
                self._flush_print()
                self._print = ("options", {"Id": int(m.group(1)), "Options": []}, line.ms)
                return
            s = text.strip()
            m = OPTION_RE.match(s)
            if m and self._print and self._print[0] == "options":
                opt: dict = {"Type_": {"PASS": 1, "END_TURN": 2, "POWER": 3}[m.group(2)]}
                if m.group(2) == "POWER":
                    opt["MainOption"] = self._sub_option(m.group(3), m.group(4), m.group(5), line)
                self._print[1]["Options"].append(opt)
                return
            m = TARGET_RE.match(s)
            if m and self._print and self._print[0] == "options" and self._print[1]["Options"]:
                opt = self._print[1]["Options"][-1]
                indent = len(text) - len(text.lstrip(" "))
                if m.group(1) == "subOption":
                    opt.setdefault("SubOptions", []).append(self._sub_option(m.group(3), m.group(4), m.group(5), line))
                else:
                    holder = opt.get("SubOptions", [None])[-1] if indent >= 6 and opt.get("SubOptions") else opt.get("MainOption")
                    if holder is None:
                        raise LogError(f"{line.where}: target line with no option")
                    target = {"Id": self._entity(m.group(3), line), "PlayError": E.play_error(m.group(4))}
                    if m.group(5):
                        target["PlayErrorParam"] = int(m.group(5))
                    holder.setdefault("Targets", []).append(target)
                return
        raise LogError(f"{line.where}: unreadable {logger} line {text!r}")

    def _sub_option(self, entity: str, error: str, param: str, line: Line) -> dict:
        so: dict = {"Id": self._entity(entity, line) if entity.strip() else 0, "PlayError": self.enums.play_error(error),
                    "PowerKeyword": 0}
        if param:
            so["PlayErrorParam"] = int(param)
        return so

    def _player_id(self, name: str, line: Line) -> int:
        eid = self._entity(name, line)
        for pid, e in self.game.players.items():
            if e == eid:
                return pid
        raise LogError(f"{line.where}: {name!r} is not a player")

    def _flush_print(self) -> None:
        if self._print is None:
            return
        kind, body, ms = self._print
        self._print = None
        game = self.game
        if game is None:
            return
        if kind == "sent":
            # A pick printed again (the hero screen can send it twice) replaces the earlier one.
            game.packets = [p for p in game.packets if not (p.type == CHOOSE_ENTITIES and not p.inferred
                                                            and p.body["Id"] == body["Id"])]
            game.packets.append(Packet(ms, "C2S", CHOOSE_ENTITIES, body))
            game.clicks_sent += 1
            return
        if kind == "choices":
            game.packets.append(Packet(ms, "S2C", ENTITY_CHOICES, body))
            if body["ChoiceType"] != CHOICE_MULLIGAN:
                self._turn_decision(ms)
        elif kind == "chosen":
            # The choice packet it answers gives the type; the player's own answer is sent a moment before.
            asked = next((p.body for p in reversed(game.packets)
                          if p.type == ENTITY_CHOICES and p.body["Id"] == body["ChooseEntities"]["Id"]), None)
            if asked is not None:
                body["ChoiceType"] = asked["ChoiceType"]
                if body["ChooseEntities"]["Entities"]:
                    game.picks.append((asked, body["ChooseEntities"]["Entities"][0]))
            game.packets.append(Packet(max(ms - CLICK_LEAD_MS, game.start_ms), "C2S", CHOOSE_ENTITIES,
                                       copy.deepcopy(body["ChooseEntities"]), inferred=True))
            game.packets.append(Packet(ms, "S2C", ENTITIES_CHOSEN, body))
        else:
            # The client reprints an id when it re-resolves targets: the later print is the fuller one.
            game.packets = [p for p in game.packets if not (p.type == ALL_OPTIONS and p.body["Id"] == body["Id"])]
            game.packets.append(Packet(ms, "S2C", ALL_OPTIONS, body))
            self._last_options_id = body["Id"]
            self._open_options = None if body["Id"] in self._answered else (body, ms)
            self._turn_decision(ms)
        game.end_ms = max(game.end_ms, ms)

    def _turn_decision(self, ms: int) -> None:
        ge = self.state.entities.get(self.game.game_entity)
        turn = ge.tags.get(TAG_TURN, 0) if ge is not None else 0
        decisions = self.game.turn_decisions
        if not decisions or decisions[-1][1] != turn:
            decisions.append((ms, turn))

    # ---- the player's clicks ------------------------------------------------------------------------------

    def _infer_click(self, entries: list[dict], ms: int) -> None:
        """The log has no SendOption lines; the block the server answered a click with names the option.
        The first block of the packet after an AllOptions, when it is a play/power/move whose source is one of
        its options, is the option taken; a step out of MAIN_ACTION with no such block is the end-turn button.
        (A turn ended on the timer reads as the button too: the log has no turn timer to tell them apart.)"""
        if self._open_options is None or self.game is None:
            return
        options, asked_ms = self._open_options
        first = next((e["PowerStart"] for e in entries if "PowerStart" in e), None)
        click = None
        if first is not None and first["Type"] not in (BLOCK_ATTACK, BLOCK_TRIGGER, BLOCK_DEATHS, BLOCK_GAME_RESET):
            index = option_index(options["Options"], first["Source"], first.get("Target", 0))
            if index is not None:
                click = {"Id": options["Id"], "Index": index, "Target": first.get("Target", 0), "SubOption": -1, "Position": 0}
        if click is None and any("TagChange" in e and e["TagChange"]["Entity"] == self.game.game_entity
                                 and e["TagChange"]["Tag"] == TAG_STEP and e["TagChange"]["Value"] == STEP_MAIN_END
                                 for e in entries):
            end = next((i for i, o in enumerate(options["Options"]) if o["Type_"] == OPTION_END_TURN), None)
            if end is not None:
                click = {"Id": options["Id"], "Index": end, "Target": 0, "SubOption": -1, "Position": 0}
        if click is None:
            return
        self._open_options = None
        self._answered.add(options["Id"])
        self.game.packets.append(Packet(max(ms - CLICK_LEAD_MS, asked_ms + 1), "C2S", CHOOSE_OPTION, click, inferred=True))
        self.game.clicks_inferred += 1

    def _sent_line(self, logger: str, line: Line) -> None:
        """The player's own clicks, printed by a client that was playing (not spectating): SendOption answers the last
        options printed; SendChoices (id, then m_chosenEntities lines) a choice."""
        game = self.game
        text = line.text.strip()
        if logger == "GameState.SendOption":
            m = SEND_OPTION_RE.match(text)
            if not m:
                raise LogError(f"{line.where}: unreadable {text!r}")
            self._flush_print()  # the options it answers may still be held, waiting for their print to end
            game.packets.append(Packet(line.ms, "C2S", CHOOSE_OPTION, {
                "Id": self._last_options_id, "Index": int(m.group(1)), "Target": int(m.group(3)),
                "SubOption": int(m.group(2)), "Position": int(m.group(4))}))
            game.clicks_sent += 1
            return
        m = SEND_CHOICES_RE.match(text)
        if m:
            self._flush_print()
            self._print = ("sent", {"Id": int(m.group(1)), "Entities": []}, line.ms)
            return
        m = CHOSEN_ENTITY_RE.match(text)
        if m and self._print and self._print[0] == "sent":
            self._print[1]["Entities"].append(self._entity(m.group(2), line))
            return
        raise LogError(f"{line.where}: unreadable {logger} line {text!r}")

    # ---- the PowerTaskList pass: notes ---------------------------------------------------------------------

    def _note(self, ms: int, text: str) -> None:
        """A recorder note. The log's times are per frame, and several blocks can start in one frame; the server
        wants a combat's note times rising, so a note never shares the previous one's time (1 ms later)."""
        notes = self.game.notes
        if notes and ms <= notes[-1][0]:
            ms = notes[-1][0] + 1
        notes.append((ms, text))

    def _executed_line(self, line: Line) -> None:
        game = self.game
        s = line.text.lstrip(" ")
        if game is None:
            return
        if s.startswith("tag="):
            if self._ptl_in_game_entity:
                m = TAG_RE.match(s)
                if m and m.group(1) in self._phase_names:
                    self._ptl_phase = int(m.group(2))
            return
        self._ptl_in_game_entity = s.startswith("GameEntity EntityID=")
        if s.startswith("BLOCK_START "):
            # Every block the client starts while in combat, nested ones too: the server takes the next note with
            # a block's type and source and skips the rest (ReplayData._match_block_notes), as with the recorder's.
            # A batched block (several sources) stands for one server block per source: a note for each.
            if self._ptl_phase == 1:
                m = BLOCK_START_RE.match(s)
                if m:
                    btype, target = self.enums.block_type(m.group(1)), self._entity(m.group(5), line)
                    for src in split_entities(m.group(2)):
                        self._note(line.ms, f"block {btype} {self._entity(src, line)} {target}")
        elif s.startswith("TAG_CHANGE Entity=GameEntity "):
            m = TAG_CHANGE_RE.match(s)
            if m and m.group(2) in self._phase_names:
                phase = int(m.group(3))
                if self._ptl_phase == 1 and phase == 0:
                    self._note(line.ms, "combat_done")
                self._ptl_phase = phase


def option_index(options: list[dict], source: int, target: int) -> int | None:
    """The option a block's source and target answer. A card can be several options (BG: a minion in hand is
    one option per way to play it), so: a playable one first, then one whose targets hold the block's target
    (no target: one without targets), then the first."""
    cands = [i for i, o in enumerate(options)
             if any(so and so.get("Id") == source for so in [o.get("MainOption")] + o.get("SubOptions", []))]
    if not cands:
        return None

    def score(i: int) -> tuple[int, int]:
        main = options[i].get("MainOption") or {}
        targets = {t.get("Id") for t in main.get("Targets", [])}
        fits = target in targets if target else not targets
        return (main.get("PlayError", 0) != 0, not fits)
    return min(cands, key=score)


# ---- writing -----------------------------------------------------------------------------------------------

def default_out_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "HS Replay Watcher" / "Logs"


def fill_player_infos(game: Game) -> None:
    """CreateGame's PlayerInfos (the log doesn't print them): one per leaderboard hero's PLAYER_ID and the local
    player's. The client needs them: the hero screen's end walks this map and waits for each opponent's hero, which
    a leaderboard hero registers by its PLAYER_ID (Entity: PLAYER_LEADERBOARD_PLACE -> SetPlayerHero); with no
    map the client never left the hero screen (2026-10-16). The account ids aren't in the log: the local player's
    comes from Players, the others' are zero (tried in game: the hero screen ends and the game plays on)."""
    ids = set()
    local_account = {"Hi": 0, "Lo": 0}
    for p in game.packets:
        if p.type != POWER_HISTORY:
            continue
        for e in p.body["List"]:
            ent = e.get("FullEntity") or e.get("ShowEntity")
            if ent:
                tags = {t["Name"]: t["Value"] for t in ent["Tags"]}
                if tags.get(TAG_LEADERBOARD_PLACE) is not None and tags.get(TAG_PLAYER_ID):
                    ids.add(tags[TAG_PLAYER_ID])
            elif "CreateGame" in e:
                for pl in e["CreateGame"]["Players"]:
                    if pl["Id"] == game.local_player:
                        local_account = pl["GameAccountId"]
    if game.local_player:
        ids.add(game.local_player)
    infos = [{"Id": i, "GameAccountId": dict(local_account if i == game.local_player else {"Hi": 0, "Lo": 0}),
              "BnetAccountId": {"Hi": 0, "Lo": 0}} for i in sorted(ids, reverse=True)]
    for p in game.packets:
        if p.type != POWER_HISTORY:
            continue
        for e in p.body["List"]:
            cg = e.get("CreateGame") or (e.get("ResetGame") or {}).get("CreateGame")
            if cg is not None:
                cg["PlayerInfos"] = [dict(i) for i in infos]


def fill_hide_chosen(game: Game) -> None:
    """EntityChoices' HideChosen isn't printed. Without it the client keeps the chosen card on screen, expecting it
    to move on. The server sets it when the chosen minion goes nowhere: in 4 recordings (72 discovers) the 2 flagged
    picks were minions whose first move after they were offered was out of the game (REMOVEDFROMGAME); the other
    chosen minions went to the hand or the board, or 2 of them to the graveyard, unflagged (trinkets and hero powers
    leave the game unflagged). Read from the power packets' own order: a spectator's log prints choices late, after
    the card has moved."""
    order = []  # (packet index, entity, kind, zone, cardtype)
    for i, p in enumerate(game.packets):
        if p.type != POWER_HISTORY:
            continue
        for e in p.body["List"]:
            for k in ("FullEntity", "ShowEntity", "ChangeEntity"):
                v = e.get(k)
                if v:
                    tags = {t["Name"]: t["Value"] for t in v["Tags"]}
                    order.append((i, v["Entity"], "offer", tags.get(TAG_ZONE), tags.get(TAG_CARDTYPE)))
            tc = e.get("TagChange")
            if tc and tc["Tag"] == TAG_ZONE:
                order.append((i, tc["Entity"], "move", tc["Value"], None))
            he = e.get("HideEntity")
            if he:
                order.append((i, he["Entity"], "move", he.get("Zone"), None))
    asked_at = {id(p.body): i for i, p in enumerate(game.packets) if p.type == ENTITY_CHOICES}
    for asked, chosen in game.picks:
        at = asked_at.get(id(asked), len(game.packets))
        offers = [k for k, (i, ent, kind, _, _) in enumerate(order) if ent == chosen and kind == "offer" and i <= at]
        if not offers:
            continue
        cardtype = next((order[k][4] for k in reversed(offers) if order[k][4] is not None), None)
        first_move = next((zone for i, ent, kind, zone, _ in order[offers[-1] + 1:] if ent == chosen and kind == "move"), None)
        if cardtype == CARDTYPE_MINION and first_move == ZONE_REMOVEDFROMGAME:
            asked["HideChosen"] = True


def drop_inferred_clicks(game: Game) -> None:
    """A log written by the playing client has the player's own clicks (SendOption / SendChoices): those replace the
    ones worked out from the answers."""
    if game.clicks_sent:
        game.packets = [p for p in game.packets if not p.inferred]
        game.clicks_inferred = 0


def fill_turn_timers(game: Game) -> None:
    """The turn timers the log doesn't print, as the server sends them (33 turns of 2 recorded games, 2026-10-16):
    one with the turn's first decision (its AllOptions, or the EntityChoices of a turn that opens with a choice),
    0.1 s after it, running out TIMER_BEFORE_COMBAT_S before the turn's combat starts; then Seconds 0 just before
    the combat (the server ending the turn)."""
    timers = []
    for ms, turn in game.turn_decisions:
        nxt = next((c for c in game.combat_starts if c > ms), None)
        if nxt is None:
            continue
        secs = int(round((nxt - ms) / 1000)) - TIMER_BEFORE_COMBAT_S
        if secs > 0:
            timers.append(Packet(ms + 100, "S2C", TURN_TIMER, {"Seconds": secs, "Turn": turn, "Show": True}))
            timers.append(Packet(max(nxt - ZERO_TIMER_BEFORE_COMBAT_MS, ms + 101), "S2C", TURN_TIMER,
                                 {"Seconds": 0, "Turn": turn, "Show": True}))
    game.packets = sorted(game.packets + timers, key=lambda p: p.ms)


def join_reconnects(games: list[Game]) -> list[Game]:
    """Games in time order, a game cut off in one file and taken up again in a later one (the client reconnected
    after a restart: the same seed's CREATE_GAME) joined into one, as one file's reconnects are."""
    out: list[Game] = []
    for g in sorted(games, key=lambda g: g.start_ms):
        prev = next((a for a in reversed(out) if g.seed and a.seed == g.seed), None)
        if prev is not None and not prev.complete and 0 <= g.start_ms - prev.end_ms <= RECONNECT_WINDOW_MS:
            prev.packets += g.packets
            prev.notes += g.notes
            prev.reconnects += [g.start_ms] + g.reconnects
            prev.complete, prev.end_ms = g.complete, max(prev.end_ms, g.end_ms)
            prev.clicks_inferred += g.clicks_inferred
            prev.combat_starts += g.combat_starts
            prev.turn_decisions += g.turn_decisions
            for k, n in g.counts.items():
                prev.counts[k] = prev.counts.get(k, 0) + n
            continue
        out.append(g)
    return out


def recording_lines(game: Game, log_name: str, build: int) -> list[dict]:
    """The recording's lines (recording.py's format) in order, ms from the handshake."""
    base = game.start_ms - OPENING_MS
    connect = {"kind": "connect", "address": "powerlog", "port": 0, "gameHandle": game.seed, "clientHandle": 0,
               "auroraPassword": "", "version": str(game.header.get("BuildNumber", build)),
               "mission": game.header.get("ScenarioID", 0), "spectatorMode": False, "reconnecting": False,
               "nextGameType": game.header.get("GameType", ""), "nextFormatType": game.header.get("FormatType", ""),
               "nextMissionId": game.header.get("ScenarioID", 0), "nextSpectator": False, "nextReconnect": False,
               "source": "powerlog", "logFile": log_name, "buildNumber": game.header.get("BuildNumber", build)}
    handshake = {"GameHandle": game.seed, "Password": "", "ClientHandle": 0, "Mission": game.header.get("ScenarioID", 0),
                 "Version": str(game.header.get("BuildNumber", build))}
    packets = [Packet(base + 500, "C2S", HANDSHAKE, handshake), Packet(base + 1000, "S2C", GAME_SETUP, GAME_SETUP_BODY),
               Packet(base + 1500, "S2C", GAME_GUARDIAN_VARS, GUARDIAN_VARS_BODY)] + game.packets
    items: list[tuple[int, int, dict]] = []  # (ms, order, line)
    for i, p in enumerate(packets):
        body = encode(PACKET_MSG[p.type], p.body)
        items.append((p.ms - base, i, {"kind": "packet", "dir": p.dir, "type": p.type, "len": len(body),
                                       "body": base64.b64encode(body).decode("ascii")}))
    for ms, text in game.notes:
        items.append((ms - base, len(packets) + len(items), {"kind": "note", "text": text}))
    for ms in game.reconnects:
        items.append((ms - base, -1, dict(connect, kind="reconnect", reconnecting=True, nextReconnect=True)))
    items.sort(key=lambda it: (it[0], it[1]))
    out = [dict(seq=0, ms=0, **connect)]
    for seq, (ms, _, line) in enumerate(items, 1):
        out.append(dict(seq=seq, ms=int(ms), **line))
    return out


def same_game_files(out_dir: Path, game: Game) -> list[Path]:
    """Converted files of this game already in out_dir: the same seed (a random 32-bit number) starting within
    SAME_GAME_MS of it. Besides an earlier conversion, these are parts of it converted from some of its log
    files only (a game cut off in one file and taken up in another)."""
    out = []
    for f in out_dir.glob(f"*_log{game.seed}.jsonl*"):
        try:
            start = dt.datetime.strptime(f.name[:15], "%Y%m%d_%H%M%S")
        except ValueError:
            continue
        if abs((start - EPOCH) / dt.timedelta(milliseconds=1) - game.start_ms) <= SAME_GAME_MS:
            out.append(f)
    return out


def write_game(game: Game, out_dir: Path, build: int, force: bool = False) -> Path | None:
    """Write the game as a recording; a game already there (or a part of it) is skipped, or with force replaced."""
    start = EPOCH + dt.timedelta(milliseconds=game.start_ms)
    path = out_dir / f"{start:%Y%m%d_%H%M%S}_log{game.seed}.jsonl.gz"
    existing = same_game_files(out_dir, game) if out_dir.is_dir() else []
    earlier = [f for f in existing if f.name[:15] < path.name[:15]]
    if earlier:
        # This is a later part of a game already there (its log's first file wasn't given): never written over it.
        print(f"  part of {', '.join(f.name for f in earlier)} (convert all of a game's log files together to join them)")
        return None
    if existing and not force:
        print(f"  already converted: {', '.join(f.name for f in existing)} (--force replaces it)")
        return None
    for f in existing:
        if f != path:
            f.unlink()
            print(f"  replaced {f.name}")
    out_dir.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".gz.tmp")
    with gzip.open(tmp, "wt", encoding="utf-8", compresslevel=6) as f:
        for line in recording_lines(game, game.file, build):
            f.write(json.dumps(line, separators=(",", ":"), ensure_ascii=False) + "\n")
    os.replace(tmp, path)
    return path


def collect(paths: list[Path]) -> list[Path]:
    """The log files to read: files as given, a folder's Power*.log. Each file is dated on its own, so the order
    doesn't matter."""
    files: list[Path] = []
    for p in paths:
        if p.is_dir():
            files += sorted((f for f in p.glob("Power*.log")), key=lambda f: f.stat().st_mtime)
        else:
            files.append(p)
    return files


def parse(paths: list[Path], enums: Enums | None = None, strict: bool = False,
          date: dt.date | None = None) -> tuple[list[Game], Parser]:
    """Every game in the logs, in time order (date: the first line's date in every file; default: first_day)."""
    parser = Parser(enums or Enums(), strict)
    for path in paths:
        first_game = len(parser.games)
        parser.start_file(path.name)
        last_ms = 0
        for line in read_lines(path):
            parser.feed(line)
            last_ms = line.ms
        day0 = first_day(path, last_ms, date)
        parser.end_file((day0 - EPOCH.date()).days * 86400000, first_game)
    games = join_reconnects(parser.finish())
    for g in games:
        fill_player_infos(g)
        fill_turn_timers(g)
        fill_hide_chosen(g)
        drop_inferred_clicks(g)
    return games, parser


def summary(game: Game) -> str:
    minutes = (game.end_ms - game.start_ms) / 60000
    clicks = f"{game.clicks_sent} clicks from the log" if game.clicks_sent else f"{game.clicks_inferred} clicks inferred"
    return (f"{game.header.get('GameType', '?')} build {game.header.get('BuildNumber', '?')} seed {game.seed}: "
            f"{len(game.packets)} packets, {game.counts.get('blocks', 0)} blocks, {clicks}, "
            f"{len(game.notes)} notes, {minutes:.1f} min, {'complete' if game.complete else 'cut off'}"
            + (f", {len(game.reconnects)} reconnect(s)" if game.reconnects else ""))


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Convert Hearthstone Power.log games into replay recordings.")
    ap.add_argument("logs", nargs="+", type=Path, help="Power.log / Power_old*.log files, or a folder of them")
    ap.add_argument("--out", type=Path, default=default_out_dir(), help="the Logs folder (default: %%LOCALAPPDATA%%\\HS Replay Watcher\\Logs)")
    ap.add_argument("--date", type=dt.date.fromisoformat, help="the date of each log's first line (default: the session folder's name, else worked back from the file's date)")
    ap.add_argument("--force", action="store_true", help="rewrite games already converted")
    ap.add_argument("--all", action="store_true", help="also convert games that aren't Battlegrounds")
    ap.add_argument("--no-scan", action="store_true", help="don't rebuild the Logs folder's catalog afterwards")
    ap.add_argument("--strict", action="store_true", help="fail on a name enums.json doesn't know instead of dropping it")
    args = ap.parse_args(argv)
    files = collect(args.logs)
    if not files:
        print("no Power*.log found")
        return 2
    enums = Enums()
    print(f"reading {', '.join(f.name for f in files)} (enums from build {enums.build})")
    try:
        games, parser = parse(files, enums, args.strict, args.date)
    except LogError as e:
        print(f"error: {e}")
        return 1
    if enums.unknown:
        print("  names enums.json doesn't know (dropped; an older or newer client's): "
              + ", ".join(f"{k} x{n}" for k, n in sorted(enums.unknown.items())))
    if parser.partial_lines:
        print(f"  skipped {parser.partial_lines} lines before a file's first CREATE_GAME (a game an earlier log file began)")
    written = 0
    for g in games:
        start = EPOCH + dt.timedelta(milliseconds=g.start_ms)
        print(f"- {start:%Y-%m-%d %H:%M} ({g.file}) {summary(g)}")
        if not g.is_battlegrounds and not args.all:
            print("  skipped: not Battlegrounds")
            continue
        if not g.local_player:
            print("  skipped: can't tell whose game it is (no single player with an account id)")
            continue
        path = write_game(g, args.out, enums.build, args.force)
        if path:
            print(f"  -> {path}")
            written += 1
    if written and not args.no_scan:
        from .catalog import scan
        for g in scan(args.out):
            if str(g.id).find("_log") >= 0:
                print(f"catalog: {g.start} {g.hero} place {g.place} turns {g.turns}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
