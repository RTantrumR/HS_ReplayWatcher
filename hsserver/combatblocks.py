"""A BG combat's fight split into one PowerHistory packet per visible block, so a replay can stop at, jump to and
step through each one.

The server sends a whole fight as one PowerHistory packet (a 17-attack combat: 58 top-level blocks in ~47 KB,
2026-10-16, game 14353), and the client animates it for 10-30 s. Cut into one packet per visible top-level block
(same recorded time), normal playback still sends them all at once, as before; a jump to block j is a snapshot of
everything before its packet, and the rest of the fight follows as the client's queue.

A block is visible when it shows something: an attack, a death phase, or a trigger/power from a card on the
board (a minion, trinket, hero, hero power, quest...). Engine blocks (sources: the game entity, a player, an
enchantment such as the 8-player shop enchant, or no entity) go with the next visible block, as does anything
between blocks; whatever precedes the first visible block goes with it. Only packets sent while
BACON_IN_COMBAT_PHASE is 1 and holding at least one ATTACK block are split: the combat's setup and cleanup
packets are engine work.
"""
from __future__ import annotations

from dataclasses import dataclass

from .gamestate import GameState
from .proto import decode, encode

POWER_HISTORY = 19
TAG_BACON_IN_COMBAT_PHASE, TAG_CARDTYPE, TAG_DEFENDING = 1522, 202, 36
BLOCK_ATTACK, BLOCK_DEATHS = 1, 6  # HistoryBlock.Type
CARDTYPE_GAME, CARDTYPE_PLAYER, CARDTYPE_ENCHANTMENT = 1, 2, 6
ENGINE_CARDTYPES = {CARDTYPE_GAME, CARDTYPE_PLAYER, CARDTYPE_ENCHANTMENT}


@dataclass
class Block:
    """One visible block of a fight, the start of its own plan packet."""
    plan_index: int  # index into the split plan of the packet this block starts
    rel: float  # recorded time of the fight packet (ms since the handshake)
    type: int  # HistoryBlock.Type of the block (1 attack, 6 deaths, 5 trigger, 3 power...)
    source: str  # card id of the block's source ("" for a death phase)
    target: str  # card id of its target (an attack's defender), or ""
    source_id: int = 0  # entity id of the source, as the PowerStart had it (matches the recorder's block notes)


def _top_level_units(entries: list[dict]) -> list[tuple[int, int, dict | None]]:
    """The packet's top-level items as (start, end, PowerStart or None): a whole block, or one bare entry."""
    units = []
    i = 0
    while i < len(entries):
        if "PowerStart" in entries[i]:
            depth, j = 0, i
            while j < len(entries):
                if "PowerStart" in entries[j]:
                    depth += 1
                elif "PowerEnd" in entries[j]:
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            units.append((i, min(j + 1, len(entries)), entries[i]["PowerStart"]))
            i = j + 1
        else:
            units.append((i, i + 1, None))
            i += 1
    return units


def _visible(gs: GameState, ps: dict | None) -> bool:
    if ps is None:
        return False
    if ps.get("Type") in (BLOCK_ATTACK, BLOCK_DEATHS):
        return True
    src = gs.entities.get(ps.get("Source", 0))
    return src is not None and bool(src.card_id) and src.tags.get(TAG_CARDTYPE) not in ENGINE_CARDTYPES


def split(plan: list[tuple]) -> tuple[list[tuple], list[Block]]:
    """The plan (rel, type, body, seq) with every fight packet cut at its visible blocks, and those blocks."""
    gs = GameState()
    out: list[tuple] = []
    blocks: list[Block] = []
    for rel, ptype, body, seq in plan:
        if ptype != POWER_HISTORY:
            out.append((rel, ptype, body, seq))
            continue
        ph = decode("PegasusGame.PowerHistory", body)
        entries = ph.get("List", [])
        ge = gs.entities.get(gs.game_entity_id or -1)
        in_combat = ge is not None and ge.tags.get(TAG_BACON_IN_COMBAT_PHASE) == 1
        units = _top_level_units(entries) if in_combat else []
        if not any(u[2] is not None and u[2].get("Type") == BLOCK_ATTACK for u in units):
            gs.apply_power_history(ph)
            out.append((rel, ptype, body, seq))
            continue
        # Chunks: each starts at a visible block (the first also takes whatever comes before it).
        chunks: list[tuple[int, int, dict | None, str, str]] = []  # start, end, PowerStart, source, target
        cur_start, cur_block = 0, None
        for start, end, ps in units:
            if _visible(gs, ps):
                src = gs.entities.get(ps.get("Source", 0))
                tgt = gs.entities.get(ps.get("Target", 0))
                if tgt is None and ps.get("Type") == BLOCK_ATTACK:
                    # An attack's header has no target: the defender is the entity set DEFENDING inside it.
                    defender = next((x["TagChange"].get("Entity") for x in entries[start:end] if "TagChange" in x
                                     and x["TagChange"].get("Tag") == TAG_DEFENDING
                                     and x["TagChange"].get("Value") == 1), None)
                    tgt = gs.entities.get(defender or -1)
                info = (ps, src.card_id if src else "", tgt.card_id if tgt else "")
                if cur_block is not None:
                    chunks.append((cur_start, start, *cur_block))
                    cur_start = start
                cur_block = info
            gs.apply_power_history({"List": entries[start:end]})
        if cur_block is None:
            out.append((rel, ptype, body, seq))
            continue
        chunks.append((cur_start, len(entries), *cur_block))
        for start, end, ps, source, target in chunks:
            blocks.append(Block(len(out), float(rel), ps.get("Type", 0), source, target, ps.get("Source", 0)))
            out.append((rel, ptype, encode("PegasusGame.PowerHistory", {"List": entries[start:end]}), seq))
    return out, blocks
