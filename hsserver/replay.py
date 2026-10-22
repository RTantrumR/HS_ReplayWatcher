"""Replay a recorded game to the real client over the game-server socket.

The client is driven into the game by the plugin's local mode, as a spectator: it can hover but sends no
moves. After the client's SpectatorHandshake, every recorded S2C packet is sent again on a virtual clock that
runs from the recorded Handshake at `speed` x real time. Client packets are logged; Ping gets an immediate Pong
(recorded Pongs are skipped), and the BG side requests the client makes per game entity are answered with the
latest recorded answer. When the server closes a connection itself it first sends
SpectatorNotify{SpectatorRemoved{GAMEOVER}}, which lets the client leave without a "connection lost" dialog.

TurnTimer packets sent late (after a jump, or while catching up) have their seconds reduced by the lateness,
so the client's turn timer shows what it showed at that point of the recorded game.

Replay points are the places a step lands on: the end of each player action's answer, each combat start, and
the first prompt after each combat. A UDP control port (fed by the plugin's keys and panel) moves through them:
    "faster" / "slower"  speed +1 / -1 (0 = paused, max 4); "speed <n>" sets it
    "step <n>"           n points forward (n > 0) or n events back (n < 0: each lands a little before its event, so
                         it plays out again; see _step_back); "next" / "back" = step 1 / step -1
    "seek <i>"           to point i (0-based)
    "time <ms> [<dir>]"  to a moment of the recording; dir (-1/0/1) is the way the user is moving, which decides
                         which edge of a combat a moment inside it lands on (see ReplayData.time_target)
    "combatdone"         the client has finished animating the combat it was sent (releases the combat hold)
    "pastpick"           the client has left the hero screen: jumps are allowed from now on
    "points <start>"     a page of the replay points with their labels, for the plugin's panel (see points_page)
    "status"             no change
Library commands (hsserver/library.py), answered with or without a game connected:
    "catalog <start>"    a page of the replayable games (see Library.catalog_page)
    "load <id>"          make game <id> the one the next connection replays; built in the background
    "library"            "library=<idle|loading|ready|error>;id=<id>;": poll after "load" until ready
One point forward plays the recorded packets out live, with their animations. Anything else (back, several
points forward, or out of a combat the client is still animating) is a jump: a GAME_RESET snapshot of the
tracked state at the target, then the pending choice, options, turn timer and BG side packets as of that point.
Jumps never go before the first action after the hero pick, and none happen until the plugin reports that the
client has left the hero screen ("pastpick"): a seek past the pick made before then plays the pick out live and
jumps the rest of the way on that report.
Each command is answered with "speed=<n>;vt=<ms>;step=<k>;total=<n>;combat=<0|1>;shown=<ms>;<status text>": the
plugin matches its animation speed and timer clock to speed/vt, puts its timeline's playhead at shown, and shows
the text.
"""
from __future__ import annotations

import asyncio
import logging
import time
from bisect import bisect_left, bisect_right
from collections import Counter
from dataclasses import dataclass
from typing import TYPE_CHECKING

from .gamestate import GameState
from .labels import label_points
from .packets import FrameReader, decode_raw, encode_varint_fields, frame, packet_name
from .proto import decode, encode
from .combatblocks import split as split_combat_blocks
from .recording import Recording

if TYPE_CHECKING:
    from .library import Library

log = logging.getLogger("replay")

HANDSHAKE, SPECTATOR_HANDSHAKE, SPECTATOR_NOTIFY, PING, PONG, USER_UI = 168, 22, 24, 115, 116, 15
CHOOSE_OPTION, CHOOSE_ENTITIES = 2, 3
TURN_TIMER, ALL_OPTIONS = 9, 14
SPECTATOR_REMOVED_GAMEOVER = 1  # SpectatorRemoved.ReasonCode: the client leaves quietly (0 = KICKED shows a popup)
POWER_HISTORY, ENTITY_CHOICES, ENTITIES_CHOSEN, PLAYER_RACES = 19, 17, 13, 36
TAG_BACON_IN_COMBAT_PHASE = 1522  # GAME_TAG.BACON_IN_COMBAT_PHASE
# Client request -> the server packet that answers it (answered with the latest one sent so far).
REQUEST_ANSWERS = {
    32: 30,  # GetGameRoundHistory -> GameRoundHistory
    33: 31,  # GetGameRealTimeBattlefieldRaces -> GameRealTimeBattlefieldRaces
    70: 69,  # RequestBattlegroundsMinionPool -> BattlegroundsMinionPool
}
COMBAT_WAIT_LIMIT_S = 120.0  # release the combat lock anyway if the client never reports
# After a rewind, the latest packet of each of these (as of the target) is sent again.
REWIND_LATEST_TYPES = {
    ALL_OPTIONS, TURN_TIMER,
    35,  # GameGuardianVars
    30,  # GameRoundHistory
    31,  # GameRealTimeBattlefieldRaces
    53,  # UpdateBattlegroundInfo
    69,  # BattlegroundsMinionPool
}
ACTION_TYPES = {CHOOSE_OPTION, CHOOSE_ENTITIES}
MAX_SPEED = 4  # the client's animations are sped up to match; beyond x4 they stop keeping up
TAG_TURN = 20  # GAME_TAG.TURN on the game entity
COMBAT_DONE_NOTE = "combat_done"  # recorder note (plugin CombatDoneNote.cs): the client finished showing a combat
BLOCK_NOTE = "block "  # recorder note "block TYPE SOURCE TARGET" (plugin CombatBlockNote.cs): the client began a block
COMBAT_BASE_MS, COMBAT_PER_ATTACK_MS, COMBAT_ESTIMATE_CAP_MS = 14000.0, 1500.0, 45000.0  # see estimate_combat_ms
HISTORY_ATTACK = 1  # HistoryBlock.Type.ATTACK
POINTS_PER_PAGE = 60  # keeps a "points" reply datagram small (~4 KB)
# A reconnect (the client crashed or dropped and joined the same game again; see docs/recorder.md): the replay plays
# up to the drop, then shows the game's "Reconnecting..." / "Reconnected" popup (plugin) for RECONNECT_HALF_S each,
# with the reset to the reconnected state between them, and goes on from there.
RECONNECT_AFTER_MS = 300.0  # the sequence starts this long after the last packet before the drop
RECONNECT_HALF_S = 1.5
RECONNECT_SETTLE_MS = 5000.0  # the reconnect has landed at its first prompt within this, else at the burst's end
RECONNECT_BURST_MS = 2000.0
# A jump is built this long after the last jump command, so a burst of clicks becomes one snapshot.
JUMP_SETTLE_S = 0.15
# A step back lands this long before the event's own start (its click, or the combat's start), never before the
# event before it, so the event plays out in front of the viewer (see ReplayData.back_landing).
BACK_LEAD_MS = 2000.0
# Where a combat's blocks sit on the timeline until the recorder notes when the client shows each (combatblocks):
# spread evenly between the boards swapping in and the fight's last moments.
BLOCK_LEAD_MS, BLOCK_LEAD_SHARE = 6000.0, 0.3
BLOCK_TAIL_MS, BLOCK_TAIL_SHARE = 4000.0, 0.15


@dataclass
class CombatResume:
    """Bounds for resuming the shop after one combat (ms, recording time); see ReplayData._combat_resume_bounds."""
    end: float  # BACON_IN_COMBAT_PHASE -> 0 was sent
    earliest: float  # first prompt after the combat (shop options or a pick)
    latest: float  # just before the player's first action after the combat
    recorded: float | None  # the original client finished the combat here (recorder note), if known
    estimated_ms: float  # how long the combat's animation probably took, from its attacks (see estimate_combat_ms)

    def resume_at(self, animated_ms: float) -> float:
        """The resume point, given how long (in original-speed ms) the watching client animated the combat."""
        if self.recorded is not None:
            return self.recorded
        return min(self.latest, max(self.earliest, self.end + animated_ms))

    @property
    def point(self) -> float:
        """The "combat end" replay point, where skipping the combat lands: the recorded moment, or the estimate."""
        return self.resume_at(self.estimated_ms)


def estimate_combat_ms(attacks: int) -> float:
    """How long the client takes to show a BG combat with this many attacks, at x1.

    Fitted on combats measured in this client (36.6): 3 attacks ~16 s, 4 ~23 s, 5 ~18 s, 10 ~29 s; the fixed part
    is the boards swapping in and the heroes walking up. Long combats speed up (BG_COMBAT_SPEED), hence the cap.
    Good to a few seconds; used only when a combat is skipped and no recorded combat_done note exists.
    """
    return min(COMBAT_ESTIMATE_CAP_MS, COMBAT_BASE_MS + COMBAT_PER_ATTACK_MS * attacks)


@dataclass
class Point:
    """A place Enter/Backspace/the panel can jump to: the end of an action's answer, a combat start/end, or where
    a reconnect has landed."""
    vt: float
    kind: str  # "action" | "combat start" | "combat block" | "combat end" | "reconnect"
    turn: int  # shop turn number
    cut: int | None = None  # "combat block": the plan packet the block starts; a jump snapshots everything before it
    block: int | None = None  # "combat block": index into ReplayData.blocks


@dataclass
class Gap:
    """A time the recorded client was away: from its last packet before a drop to where its reconnect landed (ms,
    recording time). Nothing was recorded in between; the reconnect's own burst (game setup, the whole state)
    is replaced by a reset to `land`."""
    start: float  # the last packet before the drop
    reconnect: float  # the recorder's reconnect line
    land: float  # the reconnected state is complete: its first prompt, or the end of its burst
    first: int  # plan index of the first packet after the drop


def adjust_turn_timer(body: bytes, late_ms: float) -> bytes:
    """Reduce TurnTimer.Seconds (field 1) by how late the packet is sent. Turn (2) and Show (3) are kept.

    A running timer is never cut below 1 s: if the time really ran out, the recorded Seconds=0 packet that ended
    the turn comes in the same burst and does the timeout.
    """
    if late_ms < 1000:
        return body
    try:
        fields = decode_raw(body)
    except ValueError:
        return body
    if any(kind != "varint" for _, kind, _ in fields):
        return body
    out = []
    for no, _, val in fields:
        if no == 1 and val > 0:
            val = max(1, val - int(late_ms // 1000))
        out.append((no, val))
    return encode_varint_fields(out)


class ReplayData:
    """Everything derived from a recording once per server: the S2C plan, action points, decoded PowerHistory."""

    def __init__(self, rec: Recording):
        self.rec = rec
        hs = next((p for p in rec.packets if p.dir == "C2S" and p.type == HANDSHAKE), None)
        t0 = hs.ms if hs else 0
        # (ms since the recorded handshake, type, body, seq)
        self.plan = [(p.ms - t0, p.type, p.body, p.seq) for p in rec.packets
                     if p.dir == "S2C" and p.type != PONG and p.ms >= t0]
        t = time.perf_counter()
        self.plan, self.blocks = split_combat_blocks(self.plan)
        log.info("split the combats into %d blocks in %.1f s", len(self.blocks), time.perf_counter() - t)
        self.actions = [p.ms - t0 for p in rec.packets if p.dir == "C2S" and p.type in ACTION_TYPES and p.ms >= t0]
        t = time.perf_counter()
        self.power = {i: decode("PegasusGame.PowerHistory", body)
                      for i, (_, ptype, body, _) in enumerate(self.plan) if ptype == POWER_HISTORY}
        log.info("decoded %d PowerHistory packets in %.1f s", len(self.power), time.perf_counter() - t)
        self.gaps = self._find_gaps(rec, t0)
        self.gap_first = {g.first: g for g in self.gaps}  # plan index of the first packet after a drop -> its gap
        if self.gaps:
            log.info("%d reconnect(s): %s", len(self.gaps), ", ".join(
                f"away {g.start / 1000:.1f}-{g.reconnect / 1000:.1f} s, back at {g.land / 1000:.1f} s" for g in self.gaps))
        # Combats: BACON_IN_COMBAT_PHASE 1 -> 0 on the game entity. The server sends a whole combat in < 1 s;
        # the client then animates it from its queue for many seconds.
        self.combats: list[tuple[float, float]] = []
        self.combat_end_indices: dict[int, int] = {}  # plan packet carrying BACON_IN_COMBAT_PHASE -> 0 -> combat no.
        self.turn_changes: list[tuple[float, int]] = []  # (rel, TURN on the game entity): odd = shop, even = combat
        self.game_entity: int | None = None
        self.player_entities: dict[int, int] = {}  # PlayerId -> player entity id
        self.cut_combats: set[int] = set()  # combats a drop cut short (no end recorded): they end at the drop
        start = None
        for i in sorted(self.power):
            for e in self.power[i].get("List", []):
                if "CreateGame" in e:
                    cg = e["CreateGame"]
                    if self.game_entity is not None:
                        # A reconnect's whole state: a combat still open was cut by the drop; the turn is a tag here.
                        gap = self._gap_for_index(i)
                        if start is not None and gap is not None:
                            self.cut_combats.add(len(self.combats))
                            self.combats.append((start, gap.start))
                            start = None
                        turn = next((t.get("Value", 0) for t in cg.get("GameEntity", {}).get("Tags", [])
                                     if t.get("Name") == TAG_TURN), None)
                        if turn is not None:
                            self.turn_changes.append((self.plan[i][0], turn))
                    self.game_entity = cg.get("GameEntity", {}).get("Id")
                    self.player_entities = {p.get("Id", 0): p.get("Entity", {}).get("Id", 0)
                                            for p in cg.get("Players", [])}
                tc = e.get("TagChange")
                if not tc or tc.get("Entity") != self.game_entity:
                    continue
                if tc.get("Tag") == TAG_TURN:
                    self.turn_changes.append((self.plan[i][0], tc.get("Value", 0)))
                elif tc.get("Tag") == TAG_BACON_IN_COMBAT_PHASE:
                    if tc.get("Value"):
                        start = self.plan[i][0]
                    elif start is not None:
                        self.combat_end_indices[i] = len(self.combats)
                        self.combats.append((start, self.plan[i][0]))
                        start = None
        # A combat the game ended in (BACON_IN_COMBAT_PHASE never back to 0): not in self.combats, but its blocks
        # are points too (_block_points).
        self.open_combat: float | None = start
        # The friendly player: EntityChoices go only to the player who chooses (the first one is the hero pick).
        first_choice = next((body for _, ptype, body, _ in self.plan if ptype == ENTITY_CHOICES), None)
        friendly_id = decode("PegasusGame.EntityChoices", first_choice).get("PlayerId") if first_choice else None
        self.friendly_player_entity: int | None = self.player_entities.get(friendly_id)
        # A step is one whole action: the click (C2S ChooseOption/ChooseEntities) *and* the server's full answer.
        # The answer ends when the server asks for input again (AllOptions or EntityChoices), or at the next
        # combat start (end-turn). Stepping to the click itself would show the previous action's result and
        # make every step look half an action late.
        self.action_ends = [self._answer_end(k) for k in range(len(self.actions))]
        self.combat_resume = self._combat_resume_bounds(rec, t0)
        recorded = sum(1 for r in self.combat_resume if r.recorded is not None)
        log.info("combat resume: %d of %d combats have a recorded combat_done note", recorded, len(self.combats))
        point_kinds: dict[float, str] = {e: "action" for e in self.action_ends}
        for (s, _), resume in zip(self.combats, self.combat_resume):
            point_kinds.setdefault(s, "combat start")
            # Land after a combat where the player was back in the shop (recorded, or estimated from the combat):
            # that is where skipping the combat goes.
            point_kinds.setdefault(resume.point, "combat end")
        for g in self.gaps:
            point_kinds[g.land] = "reconnect"
        self.points: list[Point] = [Point(vt, kind, self.turn_at(vt)) for vt, kind in sorted(point_kinds.items())]
        self.block_combat: dict[int, int] = {}  # plan index of a block's packet -> its combat
        self.points += self._block_points()
        self.points.sort(key=lambda p: p.vt)
        self.point_times = [p.vt for p in self.points]
        # Each combat's block points in order (key: its index in combats, None for the game's last, open combat),
        # and the point of each block's cut.
        self.combat_block_points: dict[int | None, list[int]] = {}
        self.cut_point: dict[int, int] = {}
        for i, p in enumerate(self.points):
            if p.kind == "combat block":
                self.combat_block_points.setdefault(self.block_combat.get(p.cut), []).append(i)
                self.cut_point[p.cut] = i
        # The timeline's length: the last packet, or a last combat's estimated end if that is later.
        self.end = max([float(self.plan[-1][0]) if self.plan else 0.0] + self.point_times[-1:])
        # Never rewind before the hero pick has resolved: the hero screen can't be reopened.
        self.rewind_floor = self.action_ends[0] if self.action_ends else 0.0
        self.back_landing = self._back_landings()
        log.info("%d actions, %d combats -> %d replay points", len(self.actions), len(self.combats), len(self.points))
        t = time.perf_counter()
        self.labels = label_points(self)
        log.info("labelled the replay points in %.1f s", time.perf_counter() - t)

    def _block_notes(self) -> list[tuple[float, int, int]]:
        """The recorder's block notes: (ms since the handshake, block type, source entity id), in time order."""
        hs = next((p for p in self.rec.packets if p.dir == "C2S" and p.type == HANDSHAKE), None)
        t0 = hs.ms if hs else 0
        out = []
        for n in self.rec.notes:
            text = n.get("text", "")
            if not text.startswith(BLOCK_NOTE) or n.get("ms", -1) < t0:
                continue
            f = text.split()
            try:
                out.append((float(n["ms"] - t0), int(f[1]), int(f[2])))
            except (IndexError, ValueError):
                continue
        return out

    def _block_points(self) -> list[Point]:
        """A point per visible block of each combat's fight (see combatblocks), inside the combat's span on the
        timeline: where the original client began showing it (the recorder's block notes, matched in order by type
        and source) when every block of that combat has one, else spread evenly after the boards swap in."""
        out = []
        notes = self._block_notes()
        spans = [(k, start, end, resume.point) for k, ((start, end), resume)
                 in enumerate(zip(self.combats, self.combat_resume))]
        if self.open_combat is not None:
            # The game's last combat: no end, no resume; it lasts as long as its attacks probably take.
            attacks = sum(1 for b in self.blocks if b.rel >= self.open_combat and b.type == HISTORY_ATTACK)
            spans.append((None, self.open_combat, float("inf"), self.open_combat + estimate_combat_ms(attacks)))
        for k, start, end, stop in spans:
            mine = [j for j, b in enumerate(self.blocks) if start <= b.rel <= end]
            if not mine:
                continue
            span = stop - start
            lead = min(BLOCK_LEAD_MS, BLOCK_LEAD_SHARE * span)
            tail = min(BLOCK_TAIL_MS, BLOCK_TAIL_SHARE * span)
            step = (span - lead - tail) / len(mine)
            recorded = self._match_block_notes(mine, [x for x in notes if start <= x[0] < stop])
            for n, j in enumerate(mine):
                b = self.blocks[j]
                vt = recorded[n] if recorded else start + lead + step * n
                if k is not None:
                    self.block_combat[b.plan_index] = k
                out.append(Point(vt, "combat block", self.turn_at(vt), cut=b.plan_index, block=j))
        return out

    def _match_block_notes(self, mine: list[int], notes: list[tuple[float, int, int]]) -> list[float] | None:
        """The note time of each block in mine (in order: the next note with the block's type and source), or None
        unless every block has one and the times rise."""
        times, k = [], 0
        for j in mine:
            b = self.blocks[j]
            while k < len(notes) and (notes[k][1] != b.type or notes[k][2] != b.source_id):
                k += 1
            if k == len(notes):
                return None
            times.append(notes[k][0])
            k += 1
        if any(t2 <= t1 for t1, t2 in zip(times, times[1:])):
            return None
        return times

    def _back_landings(self) -> list[float]:
        """Where a step back to each point lands (ms): BACK_LEAD_MS before the event starts (an action's first
        click, a combat's start), so it is seen happening, but never before the point before it (the previous event
        isn't replayed with it; no combat or drop lies in between) nor the hero pick's end. "combat end" and
        "reconnect" points have nothing to watch before them (a combat, a drop): they land on themselves."""
        first_click: dict[float, float] = {}
        for click, end in zip(self.actions, self.action_ends):
            first_click.setdefault(end, click)
        out = []
        for i, p in enumerate(self.points):
            if p.kind == "action":
                start = first_click.get(p.vt, p.vt)
            elif p.kind == "combat start":
                start = p.vt
            else:
                out.append(p.vt)
                continue
            before = self.point_times[i - 1] if i > 0 else self.rewind_floor
            out.append(min(p.vt, max(before, self.rewind_floor, start - BACK_LEAD_MS)))
        return out

    def points_page(self, start: int) -> str:
        """Reply to "points <start>": up to POINTS_PER_PAGE points, one per line, for the plugin's panel.

        "points=<start>;total=<n>;end=<ms>;combats=<start>-<end>,...;gaps=<start>-<land>,...;" then lines
        "index|vt ms|turn|kind|verb|card|target|arg|back ms"; kind is a (action), cs (combat start), cb (a combat
        block, see combatblocks), ce (combat end) or rc (where a reconnect landed); card/target are card ids; back is where a step back to it lands
        (back_landing). end is the timeline's length; each combat spans from its
        start to its "combat end" point (where the player was back in the shop), as the timeline shows it; each gap
        from the last packet before a drop to where its reconnect landed.
        """
        start = max(0, start)
        kinds = {"action": "a", "combat start": "cs", "combat block": "cb", "combat end": "ce", "reconnect": "rc"}
        lines = []
        for i in range(start, min(start + POINTS_PER_PAGE, len(self.points))):
            p, lab = self.points[i], self.labels[i]
            lines.append(f"{i}|{p.vt:.0f}|{p.turn}|{kinds[p.kind]}|{lab.verb}|{lab.card}|{lab.target}|{lab.arg}"
                         f"|{self.back_landing[i]:.0f}")
        combats = ",".join(f"{s:.0f}-{r.point:.0f}" for (s, _), r in zip(self.combats, self.combat_resume))
        if self.open_combat is not None:  # the game's last combat runs to the timeline's end
            combats += ("," if combats else "") + f"{self.open_combat:.0f}-{self.end:.0f}"
        gaps = ",".join(f"{g.start:.0f}-{g.land:.0f}" for g in self.gaps)
        return (f"points={start};total={len(self.points)};end={self.end:.0f};combats={combats};gaps={gaps};"
                + "\n".join(lines))

    def time_target(self, t: float, direction: int) -> float:
        """Where a seek to time t (ms) lands. Inside a combat's span (start to its "combat end" point) it lands on
        the combat's start, or on its end when moving forward; inside an action's answer (the click to the next
        prompt) on the answer's end; inside a drop (nothing recorded) where its reconnect landed; never before the
        hero pick's end or past the timeline's end."""
        t = max(self.rewind_floor, min(float(t), self.end))
        for g in self.gaps:
            if g.start < t < g.land:
                return g.land
        for (s, _), r in zip(self.combats, self.combat_resume):
            if s <= t < r.point:
                return r.point if direction > 0 else float(s)
        for click, end in zip(self.actions, self.action_ends):
            if click <= t < end:
                return end
        return t

    def _combat_resume_bounds(self, rec: Recording, t0: float) -> list["CombatResume"]:
        """Where the shop may resume after each combat (ms, recording time).

        The server sends a whole combat in under a second and the recorded shop starts right then, while the
        player's client was still animating the combat for 10-30 s; a replay that resumed the shop at the
        recorded start idled through that time. The resume point is the recorder's "combat_done" note when there
        is one (the original client finishing the combat). Otherwise a watched combat resumes as long after its
        end as the watching client took to animate it (the same animations), and a skipped one at an estimate from
        its number of attacks. Always within [first prompt after the combat, the player's first action).
        """
        notes = sorted(n["ms"] - t0 for n in rec.notes if n.get("text") == COMBAT_DONE_NOTE and n.get("ms", -1) >= t0)
        out = []
        for k, (start, end) in enumerate(self.combats):
            next_start = self.combats[k + 1][0] if k + 1 < len(self.combats) else float("inf")
            earliest = self._next_prompt_after(end)
            click = next((a for a in self.actions if a > end), None)
            latest = float(click - 1) if click is not None and click < next_start else float(next_start - 1)
            # A drop after the combat (or cutting it) ends its shop part: the replay goes on after the reconnect.
            drop = self.gap_start_after(end)
            earliest, latest = min(earliest, drop), min(latest, drop)
            latest = max(latest, earliest)
            note = next((n for n in notes if end <= n < next_start), None)
            attacks = sum(1 for i in sorted(self.power) if start <= self.plan[i][0] <= end
                          for x in self.power[i].get("List", [])
                          if x.get("PowerStart", {}).get("Type") == HISTORY_ATTACK)
            out.append(CombatResume(end=float(end), earliest=earliest, latest=latest,
                                    recorded=None if note is None else min(latest, max(earliest, float(note))),
                                    estimated_ms=estimate_combat_ms(attacks)))
        return out

    def _find_gaps(self, rec: Recording, t0: float) -> list[Gap]:
        """One Gap per reconnect line: from the last packet before it to where the reconnect landed."""
        seqs = [p[3] for p in self.plan]
        gaps = []
        for r in rec.reconnects:
            first = bisect_right(seqs, r.get("seq", -1))
            if first == 0 or first >= len(self.plan):
                continue
            start = float(self.plan[first - 1][0])
            reconnect = float(r.get("ms", 0) - t0)
            land = None
            for rel, ptype, _, _ in self.plan[first:]:
                if rel > reconnect + RECONNECT_SETTLE_MS:
                    break
                if ptype in (ALL_OPTIONS, ENTITY_CHOICES):
                    land = float(rel)
                    break
            if land is None:
                burst = [rel for rel, _, _, _ in self.plan[first:] if rel <= reconnect + RECONNECT_BURST_MS]
                land = float(burst[-1]) if burst else reconnect
            gaps.append(Gap(start=start, reconnect=reconnect, land=max(land, start), first=first))
        return gaps

    def _gap_for_index(self, i: int) -> Gap | None:
        """The last gap whose reconnect starts at or before plan index i."""
        return next((g for g in reversed(self.gaps) if g.first <= i), None)

    def gap_start_after(self, rel: float) -> float:
        """Start of the first drop at or after rel (inf if none): an action's answer or a combat's shop can't
        reach past it."""
        return next((g.start for g in self.gaps if g.start >= rel), float("inf"))

    def _next_prompt_after(self, rel: float) -> float:
        # >=: a choice offered right after a combat (a trinket pick) can arrive in the combat end's millisecond
        return float(next((r for r, ptype, _, _ in self.plan if r >= rel and ptype in (ALL_OPTIONS, ENTITY_CHOICES)),
                          rel))

    def turn_at(self, vt: float) -> int:
        """Shop turn number at vt (a combat belongs to the shop turn before it); 0 before the first turn."""
        turn = 0
        for rel, value in self.turn_changes:
            if rel > vt:
                break
            turn = value
        return (turn + 1) // 2

    def _answer_end(self, k: int) -> float:
        """Time at which the server finished answering action k (see __init__)."""
        click = self.actions[k]
        nxt = self.actions[k + 1] if k + 1 < len(self.actions) else float("inf")
        nxt = min(nxt, self.gap_start_after(click) + 0.5)  # a drop ends the answer: the rest comes after a reconnect
        asks = [rel for rel, ptype, _, _ in self.plan
                if click < rel < nxt and ptype in (ALL_OPTIONS, ENTITY_CHOICES)]
        starts = [s for s, _ in self.combats if click < s < nxt]
        candidates = asks[:1] + starts[:1]
        if candidates:
            return float(min(candidates))
        # No new prompt before the next click: take the last packet of the answer.
        answer = [rel for rel, _, _, _ in self.plan if click < rel < nxt]
        return float(answer[-1]) if answer else float(click)

    def index_after(self, vt: float) -> int:
        """Index of the first plan packet due after vt."""
        return next((i for i, p in enumerate(self.plan) if p[0] > vt), len(self.plan))

    def latest_before(self, ptype: int, idx: int) -> bytes | None:
        """Body of the last packet of this type among plan[:idx]."""
        for i in range(idx - 1, -1, -1):
            if self.plan[i][1] == ptype:
                return self.plan[i][2]
        return None

    def rewind_packets(self, vt: float, forward: bool = False, end: int | None = None) -> list[tuple[int, bytes]]:
        """Packets that put a client into the recorded state at vt (all packets with rel <= vt applied), or, with
        end, the state before plan packet end (a combat block: its packets share the fight's recorded time).

        Backward jumps use the game entity as the reset's source (the client's "opponent" rewind wipe), forward
        jumps the friendly player entity (the other wipe), so the two directions look different.
        """
        end = self.index_after(vt) if end is None else end
        gs = GameState()
        latest: dict[int, tuple[float, bytes]] = {}
        races: list[bytes] = []
        choices: dict[int, bytes] = {}  # EntityChoices by id, removed when an EntitiesChosen with that id arrives
        for i in range(end):
            rel, ptype, body, _ = self.plan[i]
            if ptype == POWER_HISTORY:
                gs.apply_power_history(self.power[i])
            elif ptype == ENTITY_CHOICES:
                choices[decode("PegasusGame.EntityChoices", body).get("Id", 0)] = body
            elif ptype == ENTITIES_CHOSEN:
                # The id is nested: EntitiesChosen{ChooseEntities{Id, Entities}, PlayerId, ChoiceType}
                chosen = decode("PegasusGame.EntitiesChosen", body).get("ChooseEntities", {})
                choices.pop(chosen.get("Id", 0), None)
            elif ptype == PLAYER_RACES:
                races.append(body)
            elif ptype in REWIND_LATEST_TYPES:
                latest[ptype] = (rel, body)
        source = self.friendly_player_entity if forward else None
        out = [(POWER_HISTORY, gs.snapshot_power_history(rewind=True, source=source))]
        out += [(ENTITY_CHOICES, b) for b in choices.values()]
        if ALL_OPTIONS in latest:
            out.append((ALL_OPTIONS, latest.pop(ALL_OPTIONS)[1]))
        if TURN_TIMER in latest:
            rel, body = latest.pop(TURN_TIMER)
            out.append((TURN_TIMER, adjust_turn_timer(body, vt - rel)))
        out += [(ptype, body) for ptype, (_, body) in sorted(latest.items())]
        out += [(PLAYER_RACES, b) for b in races]
        return out


class ReplaySession:
    def __init__(self, data: ReplayData, speed: float, quiet_types: set[int]):
        self.data = data
        self.plan = data.plan
        self.points = data.points
        self.speed = speed
        # Combat lock: after sending a combat's end, hold packets and clock until the client says it has
        # finished animating that combat ("combatdone", sent by the plugin when the executed
        # BACON_IN_COMBAT_PHASE on the game entity goes 1 -> 0).
        self.combat_ends_sent = 0
        self.combat_ends_seen = 0
        self._combat_wait_started: float | None = None
        self._held_combat: int | None = None  # index into data.combats of the combat being held for
        self._held_animated_ms = 0.0  # how long the client has been animating it, in original-speed ms
        self.quiet_types = quiet_types
        self.received = Counter()
        self.sent = 0
        self.idx = 0  # next plan packet to send
        self.vt = 0.0  # virtual clock, ms since the recorded Handshake
        self._last: float | None = None  # monotonic time vt was last advanced; None until the replay starts
        # Snapshot jump (target vt, forward?) requested by a command and carried out by the replay loop once
        # JUMP_SETTLE_S has passed since the last jump command: commands that arrive before then overwrite it, so a
        # burst of clicks sends one snapshot. forward? is judged from where the burst started (_jump_from), not
        # from the previous click's target: it only picks the wipe variant.
        self._jump: tuple[float, bool] | None = None
        self._jump_at = 0.0  # monotonic time of the last jump command
        self._jump_from = 0.0  # vt when the current burst of jump commands started
        self._live_target: float | None = None  # target of the last live (played-out) step
        # The point the last step back went to (it lands before it): while it hasn't been reached again, the next step
        # back goes on from it, so repeated presses walk back event by event instead of re-landing on one.
        self._back_anchor: int | None = None
        self._live_move = False  # the command being handled plays packets out live (a burst follows the reply)
        # The hero screen can't be rebuilt by a snapshot, so no jump may happen before the client has left it
        # ("pastpick" from the plugin). A seek past the pick made before then plays the pick out live and jumps
        # the rest of the way once the client reports.
        self._pick_done = False
        self._after_pick: float | None = None
        self._after_pick_cut: int | None = None  # the parked target's combat block cut (see Point.cut)
        self._reconnecting = False  # the reconnect sequence is playing (the clock stands still)
        # After a jump to a combat block: how far into the combat's animation it lands (original-speed ms after the
        # combat's end was sent), taken as the held combat's animated time once the rest of the fight is sent.
        self._pending_animated: float | None = None
        # Walking a combat while paused: blocks the last step moved over without a jump (the plugin plays the client
        # on until that many visible blocks have started, then freezes it again); whether the last move jumped to a
        # combat block (the plugin then freezes the client once the reset is over, instead of when it is idle).
        self._walk = 0
        self._block_jump = False
        self._moved = False  # the command being answered moves the replay (mv=1 in the reply)
        # The combat block the client last reported starting ("blockat"; a point index), and the first point a
        # report may match (the block a jump landed before, else the combat's first block). A walk aims past the
        # last report or the last walk's target, whichever is further.
        self._block_at: int | None = None
        self._block_from: int | None = None
        self._walk_target: int | None = None
        # A jump to a combat block while paused holds the rest of the fight back (_rest_cut: that block's packet):
        # the client stands before the block with nothing queued. A step forward releases it and walks (the block
        # plays, the client stops at the next; fresh=1 tells the plugin the first block start isn't a stop), as
        # does playing on.
        self._rest_cut: int | None = None
        self._fresh = False
        self._wake = asyncio.Event()

    # ---- control ------------------------------------------------------------------------------------

    @property
    def waiting_for_combat(self) -> bool:
        return self.combat_ends_seen < self.combat_ends_sent

    def _advance(self) -> None:
        """Bring vt up to now at the current speed. Must run before any speed or clock change.
        The clock stands still while the client is still animating a combat (timers freeze with it)."""
        if self._last is None:
            return
        now = time.monotonic()
        if self._reconnecting:
            pass
        elif not self.waiting_for_combat:
            self.vt += (now - self._last) * 1000.0 * self.speed
        else:
            # The client animates the held combat at the replay speed (frozen while paused): measure it in
            # original-speed time, which is how long the original player spent watching it.
            self._held_animated_ms += (now - self._last) * 1000.0 * self.speed
        self._last = now

    def _request_jump(self, vt: float, cut: int | None = None) -> None:
        """Ask the replay loop for a snapshot at vt (see _jump); cut: a combat block's packet to land before."""
        if self._jump is None:
            self._jump_from = self.vt
        self._jump = (vt, vt > self._jump_from, cut)
        self._block_jump = cut is not None
        self._jump_at = time.monotonic()
        self._live_target = None
        self.vt = vt

    def control(self, cmd: str) -> str:
        self._advance()
        name, _, arg = cmd.partition(" ")
        self._live_move = False
        self._walk = 0
        self._block_jump = False
        self._fresh = False
        self._moved = name in ("next", "back", "step", "seek", "time")
        was_speed = self.speed
        try:
            if name == "faster":
                self.speed = min(MAX_SPEED, int(self.speed) + 1)
            elif name == "slower":
                self.speed = max(0, int(self.speed) - 1)
            elif name == "speed":
                self.speed = max(0, min(MAX_SPEED, int(arg)))
            elif name in ("next", "back", "step"):
                self._step({"next": 1, "back": -1}.get(name) or int(arg))
            elif name == "seek":
                self._go(int(arg), backward=None)
            elif name == "time":
                ms, _, direction = arg.partition(" ")
                self._go_time(float(ms), int(direction or 0))
            elif name == "pastpick":
                self._pick_done = True
                self._back_anchor = None
                if self._after_pick is not None:
                    target, self._after_pick = self._after_pick, None
                    if target > self.vt:
                        log.info("hero pick done: jumping on to %.1f s", target / 1000)
                        self._request_jump(target, self._after_pick_cut)
            elif name == "blockat":
                btype, _, source = arg.partition(" ")
                self._on_block_at(int(btype), int(source))
            elif name == "combatdone":
                self.combat_ends_seen = min(self.combat_ends_seen + 1, self.combat_ends_sent)
                if self._combat_wait_started is not None and not self.waiting_for_combat:
                    log.info("client finished the combat after %.1f s", time.monotonic() - self._combat_wait_started)
                    self._release_combat()
            elif name != "status":
                return f"unknown command {cmd!r}"
            if was_speed > 0 and self.speed == 0:
                self._pause_at_block()
            if self.speed > 0 and self._rest_cut is not None:
                log.info("playing on: the rest of the fight follows")
                self._rest_cut = None
        except ValueError:
            return f"bad command {cmd!r}"
        # Only a time seek's burst is raced through by the plugin; a step's (Enter) is watched with its animations.
        self._live_move = self._live_move and name == "time"
        self._wake.set()
        return self.status()

    def _release_combat(self) -> None:
        """The client has shown the held combat: continue from where the original player was back in the shop
        (skipping the time the original client spent animating it), or further if a live step aimed past it."""
        self._combat_wait_started = None
        self._block_at = self._block_from = self._walk_target = None
        if self._held_combat is not None:
            bounds = self.data.combat_resume[self._held_combat]
            resume = bounds.resume_at(self._held_animated_ms)
            log.info("shop resumes %.1f s after the combat's end (%s; client animated it for %.1f s at x1)",
                     (resume - bounds.end) / 1000, "recorded" if bounds.recorded is not None else "measured",
                     self._held_animated_ms / 1000)
            self.vt = max(self.vt, resume)
            self._held_combat = None
        if self._live_target is not None and self.vt < self._live_target:
            self.vt = self._live_target

    def _pause_at_block(self) -> None:
        """Paused in a fight: the client finishes the block it is playing and holds the next one (the plugin), so
        the position is the last block it began (or a walk's target), not the clock's estimate, which may have run
        past it: a step back from there replays that block."""
        showing, combat = self._combat_now()
        known = [i for i in (self._block_at, self._walk_target) if i is not None]
        if not showing or not known:
            return
        vt = self.points[max(known)].vt
        if combat is None:
            self.vt = min(self.vt, vt)
        else:
            self._held_animated_ms = min(self._held_animated_ms, vt - self.data.combat_resume[combat].end)
        log.info("paused in a fight: position set to point %d, the last block the client began", max(known))

    def _position(self) -> tuple[int, bool]:
        """(index of the last point reached, -1 before the first; whether vt sits on that point). In a combat the
        client is animating, the position is how far it has got (shown), not the held clock."""
        times = self.data.point_times
        at = self.shown() if self.waiting_for_combat else self.vt
        last = bisect_right(times, at) - 1
        return last, last >= 0 and times[last] >= at - 1

    def _step(self, n: int) -> None:
        if n > 0:
            if self.speed == 0 and self._walk_blocks(n):
                return
            last, _ = self._position()
            self._go(last + n, backward=False)
        elif n < 0:
            self._step_back(-n)

    def _walk_blocks(self, n: int) -> int:
        """Paused in a combat the client is showing: move over up to n of its next block points without a jump (the
        plugin plays the client on to them). Returns how many; 0 when the next point isn't one of this combat's
        blocks (a step then works as anywhere else)."""
        if self._rest_cut is not None:
            return self._walk_from_rest(n)
        if self.waiting_for_combat and self._held_combat is not None:
            combat = self._held_combat
        elif self.data.open_combat is not None and self.vt >= self.data.open_combat and self.idx >= len(self.plan):
            combat = None  # the game's last combat, fully sent (it is never held)
        else:
            return 0
        last, _ = self._position()
        known = [i for i in (self._block_at, self._walk_target) if i is not None]
        if known:
            last = max(known)
        target, walked = -1, 0
        for i in range(last + 1, len(self.points)):
            p = self.points[i]
            if p.kind != "combat block" or self.data.block_combat.get(p.cut) != combat:
                break
            target, walked = i, walked + 1
            if walked == n:
                break
        if not walked:
            return 0
        vt = self.points[target].vt
        if combat is None:
            self.vt = vt
        else:
            self._held_animated_ms = vt - self.data.combat_resume[combat].end
        self._back_anchor = None
        self._live_target = None
        self._walk = walked
        self._walk_target = target
        log.info("paused walk: %d block(s) from point %d to %d", walked, last, target)
        return walked

    def _walk_from_rest(self, n: int) -> int:
        """Release the fight held before a block (see _rest_cut) and walk n blocks: that block and the ones after
        it play, the client stops at the start of the block after the last. At the fight's last block it plays out."""
        cut, self._rest_cut = self._rest_cut, None
        combat = self.data.block_combat.get(cut)
        points = self.data.combat_block_points.get(combat, [])
        j = self.data.cut_point.get(cut)
        if j not in points:
            return 0
        pos = points.index(j)
        walked = min(n, len(points) - 1 - pos)
        if walked <= 0:
            log.info("paused walk at the fight's last block: it plays out")
            return 0
        target = points[pos + walked]
        vt = self.points[target].vt
        if combat is None:
            self.vt = vt
        else:
            self._pending_animated = vt - self.data.combat_resume[combat].end
        self._back_anchor = None
        self._live_target = None
        self._walk, self._walk_target, self._fresh = walked, target, True
        log.info("paused walk: released the fight at point %d, %d block(s) to %d", j, walked, target)
        return walked

    def _combat_now(self) -> tuple[bool, int | None]:
        """(whether the client is showing a combat's fight, which: its index, or None for the game's last)."""
        if self.waiting_for_combat and self._held_combat is not None:
            return True, self._held_combat
        if self.data.open_combat is not None and self.vt >= self.data.open_combat and self.idx >= len(self.plan):
            return True, None
        return False, None

    def _on_block_at(self, btype: int, source: int) -> None:
        """The client began a visible block of the fight it shows (plugin, from the PowerStart's type and source):
        find it from the last report on (the same source can act again later) and move the position there."""
        showing, combat = self._combat_now()
        points = self.data.combat_block_points.get(combat, []) if showing else []
        if not points:
            return
        start = 0
        if self._block_at is not None and self._block_at in points:
            start = points.index(self._block_at) + 1
        elif self._block_from is not None and self._block_from in points:
            start = points.index(self._block_from)
        for i in points[start:]:
            b = self.data.blocks[self.points[i].block]
            if b.type == btype and b.source_id == source:
                self._block_at = i
                vt = self.points[i].vt
                log.debug("client began block point %d (type %d, source %d) at %.1f s", i, btype, source, vt / 1000)
                if combat is None:
                    self.vt = max(self.vt, vt)
                else:
                    self._held_animated_ms = max(self._held_animated_ms, vt - self.data.combat_resume[combat].end)
                return
        log.info("client began a block (type %d, source %d) not found after point %s", btype, source, self._block_at)

    def _step_back(self, k: int) -> None:
        """k events back, each landing before its event (ReplayData.back_landing) so it plays out again: the first
        is the last event reached, or, right after a step back (its event not reached again), the one before it."""
        times = self.data.point_times
        if not times or not self._pick_done:
            return  # the hero screen can't be rebuilt (see _go)
        anchor = self._back_anchor
        if anchor is not None and self.vt <= times[anchor] + 1:
            target = anchor - k
        else:
            last, _ = self._position()
            target = last + 1 - k
        target = max(bisect_left(times, self.data.rewind_floor), min(target, len(times) - 1))
        land = self.data.back_landing[target]
        if abs(land - self.vt) < 1:
            return  # already there: nothing earlier to go back to
        self._after_pick = None
        point = self.points[target]
        self._request_jump(land, point.cut if abs(land - point.vt) < 1 else None)
        self._back_anchor = target

    def _go(self, target: int, backward: bool | None) -> None:
        """Move to point `target`: play it out live when it is the very next point, else jump by snapshot."""
        times = self.data.point_times
        self._back_anchor = None
        if not times:
            return
        floor = bisect_left(times, self.data.rewind_floor)
        target = max(floor, min(target, len(times) - 1))
        vt = times[target]
        last, on = self._position()
        self._after_pick = None
        if not self._pick_done:
            # Still on (or leaving) the hero screen: forward only, and only live up to the pick.
            if vt <= self.vt:
                return
            if vt > self.data.rewind_floor:
                self._after_pick = vt
                self._after_pick_cut = self.points[target].cut
                vt = max(self.vt, self.data.rewind_floor)
            self._live_target = self.vt = vt
            return
        if backward and vt >= self.vt:
            return  # nothing earlier to go back to
        if target == last and on:
            return
        if vt > self.vt and target == last + 1 and not self.waiting_for_combat:
            # Live: the recorded packets play out with their animations.
            self._live_target = self.vt = vt
            self._live_move = True
        else:
            # Snapshot: back, more than one point ahead, or out of a combat the client is still animating.
            self._request_jump(vt, self.points[target].cut)

    def _go_time(self, t: float, direction: int) -> None:
        """Move to time t (ms; see ReplayData.time_target for where it lands). A target on a replay point is a
        seek to that point; any other moves live when it is ahead and crosses no point, else jumps."""
        times = self.data.point_times
        self._back_anchor = None
        if not times:
            return
        t = self.data.time_target(t, direction)
        i = bisect_left(times, t)
        if not self._pick_done or (i < len(times) and abs(times[i] - t) < 1):
            # Before the pick is done only points are handled (see _go); a point is a seek.
            self._go(min(i, len(times) - 1), backward=None)
            return
        if abs(t - self.vt) < 1:
            return
        crossed = bisect_right(times, t) - bisect_right(times, self.vt)
        self._after_pick = None
        if t > self.vt and crossed == 0 and not self.waiting_for_combat:
            # Nothing new to animate before t (no prompt answered in between): just let the clock run to it.
            self._live_target = self.vt = t
            self._live_move = True
        else:
            # Anything that would play packets out (the client would then animate them for seconds) is a jump.
            self._request_jump(t)

    def shown(self) -> float:
        """The replay's position on the timeline: vt, except while the client animates a combat, when it moves
        through the combat's span as the client plays it (up to the span's end)."""
        if self.waiting_for_combat and self._held_combat is not None:
            r = self.data.combat_resume[self._held_combat]
            return min(r.point, r.end + self._held_animated_ms)
        return self.vt

    def state_prefix(self) -> str:
        """Machine-readable head of every control reply, parsed by the plugin: ... live=1 when the command just
        handled plays packets out live (a burst follows this reply; the plugin races through it); pend=<ms> is the
        target parked until the hero pick is over (-1 none); walk=<n> blocks a paused step moved over
        without a jump (_walk_blocks); blk=1 when the command jumps to a combat block; mv=1 in the reply to a move; fresh=1 when the walk
        releases a fight held before its block (that block's start is not a stop)."""
        last, _ = self._position()
        pend = self._after_pick if self._after_pick is not None else -1
        return (f"speed={self.speed:g};vt={self.vt:.0f};step={last + 1};total={len(self.points)};"
                f"combat={int(self.waiting_for_combat)};shown={self.shown():.0f};rc={int(self._reconnecting)};"
                f"live={int(self._live_move)};pend={pend:.0f};walk={self._walk};blk={int(self._block_jump)};mv={int(self._moved)};fresh={int(self._fresh)};")

    def _due(self, idx: int) -> float:
        """When plan packet idx is due: its recorded time, except the first packet after a drop, which is due
        just after the drop (the time away is skipped; that packet starts the reconnect sequence)."""
        gap = self.data.gap_first.get(idx)
        return gap.start + RECONNECT_AFTER_MS if gap is not None else self.plan[idx][0]

    def _after_snapshot(self) -> None:
        """A snapshot replaces whatever combat was playing: nothing left to wait for."""
        self._block_at = self._block_from = self._walk_target = None
        self.combat_ends_sent = self.combat_ends_seen = sum(1 for i in self.data.combat_end_indices if i < self.idx)
        self._combat_wait_started = None
        self._held_combat = None

    async def _play_reconnect(self, gap: Gap, send) -> None:
        """The recorded client dropped here: the plugin shows the game's "Reconnecting..." popup, then the reset to
        the reconnected state (a forward rewind wipe) behind its "Reconnected" one; the clock stands still
        throughout. A move made meanwhile (a jump) takes over instead of the reset."""
        log.info("the recorded client dropped at %.1f s; reconnect lands at %.1f s", gap.start / 1000, gap.land / 1000)
        self._reconnecting = True
        try:
            await asyncio.sleep(RECONNECT_HALF_S)
            if self._jump is None:
                for ptype, body in self.data.rewind_packets(gap.land, forward=True):
                    await send(ptype, body)
                self.idx = self.data.index_after(gap.land)
                self.vt = max(self.vt, gap.land)
                self._after_snapshot()
                log.info("reconnected: reset to %.1f s", gap.land / 1000)
                await asyncio.sleep(RECONNECT_HALF_S)
            else:
                log.info("reconnect cut short by a move")
        finally:
            self._reconnecting = False
            self._last = time.monotonic()

    def status(self) -> str:
        last, _ = self._position()
        speed = "paused" if self.speed == 0 else f"x{self.speed:g}"
        text = f"Replay {speed} | step {last + 1}/{len(self.points)}"
        if last >= 0:
            point = self.points[last]
            text += f" ({point.kind}) | turn {point.turn}"
        if self.waiting_for_combat:
            text += " | combat playing"
        if self._after_pick is not None:
            text += " | jumping on after the hero pick"
        return text

    # ---- connection ---------------------------------------------------------------------------------

    async def run(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer = writer.get_extra_info("peername")
        log.info("client connected from %s", peer)
        frames = FrameReader()
        handshake = asyncio.Event()
        send_lock = asyncio.Lock()

        async def send(ptype: int, body: bytes) -> None:
            async with send_lock:
                writer.write(frame(ptype, body))
                await writer.drain()

        client_closed = False

        async def read_loop() -> None:
            nonlocal client_closed
            while True:
                data = await reader.read(65536)
                if not data:
                    log.info("client closed the connection")
                    client_closed = True
                    return
                for pk in frames.feed(data):
                    self.received[pk.type] += 1
                    if pk.type in (HANDSHAKE, SPECTATOR_HANDSHAKE):
                        log.info("C2S %s (%d bytes)", pk.name, len(pk.body))
                        if pk.type == HANDSHAKE:
                            log.warning("player handshake: this server replays to spectators; the client may act")
                        handshake.set()
                    elif pk.type == PING:
                        await send(PONG, b"")
                    elif pk.type in REQUEST_ANSWERS:
                        # The client asks for these at game start and again after every reset (rewind).
                        answer_type = REQUEST_ANSWERS[pk.type]
                        body = self.data.latest_before(answer_type, self.idx)
                        if body is not None:
                            await send(answer_type, body)
                        log.info("C2S %s -> %s", pk.name, packet_name(answer_type) if body is not None else "nothing yet")
                    elif pk.type not in self.quiet_types:
                        log.info("C2S %s (%d) %d bytes", pk.name, pk.type, len(pk.body))

        async def replay_loop() -> None:
            await handshake.wait()
            plan = self.plan
            log.info("replaying %d S2C packets, %d replay points (speed x%g, ~%.0f s)",
                     len(plan), len(self.points), self.speed, plan[-1][0] / 1000 if plan else 0)
            self.idx = 0
            self._last = time.monotonic()
            finished_logged = False
            while True:  # runs until the client disconnects; at the end it only waits for Backspace
                self._advance()
                if self._jump is not None:
                    # Let a burst of clicks settle into one target (nothing is sent meanwhile).
                    settle = JUMP_SETTLE_S - (time.monotonic() - self._jump_at)
                    if settle > 0:
                        await asyncio.sleep(settle)
                        continue
                    (target, forward, cut), self._jump = self._jump, None
                    t = time.perf_counter()
                    packets = self.data.rewind_packets(target, forward, end=cut)
                    for ptype, body in packets:
                        await send(ptype, body)
                    self.idx = self.data.index_after(target) if cut is None else cut
                    self._after_snapshot()
                    self._pending_animated = None
                    self._rest_cut = cut if cut is not None and self.speed == 0 else None
                    if cut is not None and cut in self.data.block_combat:
                        # The rest of the fight follows at once (its recorded time is behind vt); the combat is
                        # then held from where this block sits on the timeline.
                        combat_end = self.data.combat_resume[self.data.block_combat[cut]].end
                        self._pending_animated = max(0.0, target - combat_end)
                    if cut is not None:
                        self._block_from = self.data.cut_point.get(cut)
                    log.info("jumped %s to %.1f s: %d packets (%.0f KB snapshot) in %.2f s",
                             "forward" if forward else "back", target / 1000, len(packets),
                             len(packets[0][1]) / 1024, time.perf_counter() - t)
                if (self.waiting_for_combat and self._combat_wait_started is not None
                        and time.monotonic() - self._combat_wait_started > COMBAT_WAIT_LIMIT_S):
                    log.warning("no combat-finished report after %.0f s; continuing", COMBAT_WAIT_LIMIT_S)
                    self.combat_ends_seen = self.combat_ends_sent
                    self._release_combat()
                while (self.idx < len(plan) and self._due(self.idx) <= self.vt
                       and not self.waiting_for_combat and self._jump is None and self._rest_cut is None):
                    gap = self.data.gap_first.get(self.idx)
                    if gap is not None:
                        await self._play_reconnect(gap, send)
                        if self.idx in self.data.gap_first:
                            break  # a jump took over: it lands elsewhere (the loop's top carries it out)
                        continue
                    rel, ptype, body, seq = plan[self.idx]
                    if ptype == TURN_TIMER:
                        body = adjust_turn_timer(body, self.vt - rel)
                    await send(ptype, body)
                    if self.idx in self.data.combat_end_indices:
                        # Combat fully sent; hold the next shop (packets and clock) until the client has played it.
                        self.combat_ends_sent += 1
                        self._held_combat = self.data.combat_end_indices[self.idx]
                        self._held_animated_ms = self._pending_animated or 0.0
                        self._pending_animated = None
                        self.vt = float(rel)
                        self._combat_wait_started = time.monotonic()
                        log.info("combat sent (ends at %.1f s); holding until the client finishes it", rel / 1000)
                    self.idx += 1
                    self.sent += 1
                    if ptype not in self.quiet_types:
                        log.debug("S2C #%d %s %d bytes", seq, packet_name(ptype), len(body))
                    if self.sent % 250 == 0:
                        log.info("sent %d/%d packets", self.sent, len(plan))
                if self.idx >= len(plan) and not finished_logged:
                    log.info("replay reached the end: %d packets sent (Backspace still works)", self.sent)
                    finished_logged = True
                elif self.idx < len(plan):
                    finished_logged = False
                # Sleep until the next packet is due, or until a control command changes the clock/speed.
                due = self.idx < len(plan) and self.speed > 0
                if self.waiting_for_combat:
                    timeout = 1.0  # woken by "combatdone"; poll only for the safety limit
                else:
                    timeout = (self._due(self.idx) - self.vt) / 1000.0 / self.speed if due else None
                self._wake.clear()
                try:
                    await asyncio.wait_for(self._wake.wait(), timeout)
                except asyncio.TimeoutError:
                    pass

        reading = asyncio.create_task(read_loop())
        replaying = asyncio.create_task(replay_loop())
        try:
            done, pending = await asyncio.wait({reading, replaying}, return_when=asyncio.FIRST_COMPLETED)
            if replaying in done and not reading.done():
                # Keep answering pings until the client leaves.
                await reading
            for t in done:
                if t.exception():
                    raise t.exception()
        except (ConnectionResetError, BrokenPipeError) as e:
            log.info("connection lost: %s", e)
            client_closed = True
        finally:
            replaying.cancel()
            reading.cancel()
            if not client_closed:
                # We are the ones closing (server shutting down): tell the spectator the game is over first.
                try:
                    writer.write(frame(SPECTATOR_NOTIFY, encode(
                        "PegasusGame.SpectatorNotify",
                        {"SpectatorRemoved": {"ReasonCode": SPECTATOR_REMOVED_GAMEOVER}})))
                except Exception as e:  # best effort: the socket may already be gone
                    log.debug("SpectatorNotify on close failed: %s", e)
            writer.close()
            summary = ", ".join(f"{packet_name(t)} x{n}" for t, n in self.received.most_common())
            log.info("session over: sent %d S2C; received %s", self.sent, summary or "nothing")


class ControlProtocol(asyncio.DatagramProtocol):
    """One UTF-8 command per datagram; replies with the session status (or library/catalog/points data)."""

    def __init__(self, sessions: set[ReplaySession], library: "Library"):
        self.sessions = sessions
        self.library = library
        self.transport: asyncio.DatagramTransport | None = None

    def connection_made(self, transport) -> None:
        self.transport = transport

    def _page_start(self, arg: str) -> int:
        try:
            return int(arg or 0)
        except ValueError:
            return 0

    def datagram_received(self, data: bytes, addr) -> None:
        raw = data.decode("utf-8", "replace").strip()
        name, _, arg = raw.partition(" ")
        name, arg = name.lower(), arg.strip()
        # Library commands work with or without a game connected.
        if name in ("points", "catalog", "load", "library"):
            if name == "points":
                # The connected game's points (its session may be on an older game than the library's current).
                session = next(iter(self.sessions), None)
                game = session.data if session else self.library.current
                reply = game.points_page(self._page_start(arg)) if game else "points=0;total=0;"
            elif name == "catalog":
                reply = self.library.catalog_page(self._page_start(arg))
            elif name == "load":
                reply = self.library.request(arg)
            else:
                reply = self.library.status()
            log.log(logging.DEBUG if name in ("points", "library") else logging.INFO,
                    "control %r -> %s", raw, reply if len(reply) < 120 else f"{len(reply)} bytes")
            self.transport.sendto(reply.encode("utf-8"), addr)
            return
        cmd = raw.lower()
        if not self.sessions:
            reply = "speed=1;vt=0;Replay: no game connected"
        else:
            texts = [s.control(cmd) for s in self.sessions]
            first = next(iter(self.sessions))
            # speed/vt let the plugin match client animations and its timer clock to the replay position.
            reply = first.state_prefix() + " / ".join(texts)
        log.info("control %r -> %s", cmd, reply)
        self.transport.sendto(reply.encode("utf-8"), addr)


async def serve(library: "Library", host: str, port: int, speed: float, quiet_types: set[int],
                control_port: int) -> None:
    sessions: set[ReplaySession] = set()

    async def on_client(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        data = library.current
        if data is None:
            log.warning("client connected but no replay is loaded (library %s); closing", library.status())
            writer.close()
            return
        log.info("client connected: replaying %s", library.current_id)
        session = ReplaySession(data, speed, quiet_types)
        sessions.add(session)
        try:
            await session.run(reader, writer)
        finally:
            sessions.discard(session)

    loop = asyncio.get_running_loop()
    transport, _ = await loop.create_datagram_endpoint(lambda: ControlProtocol(sessions, library),
                                                       local_addr=(host, control_port))
    server = await asyncio.start_server(on_client, host, port)
    log.info("replay server for %s listening on %s:%d (control udp %d)",
             library.current_id or library.folder, host, port, control_port)
    await library.rescan()
    try:
        async with server:
            await server.serve_forever()
    finally:
        transport.close()
