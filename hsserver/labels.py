"""Names for replay points ("buy Dune Dweller", "combat vs Sylvanas"), for the plugin's replay panel.

Each label is (verb, card, target, arg): card/target are card ids the plugin turns into localized names, arg is
a short extra (a tavern tier). Actions are classified from the clicked option and the state when it was clicked:

    TB_BaconShop_DragBuy[_Spell] + target   buy <target>
    TB_BaconShop_DragSell + target          sell <target>
    TB_BaconShopTechUpNN_Button             upgrade (to tier NN)
    TB_BaconShop_8p_Reroll_Button           refresh
    *LockAll* / *Freeze* button             freeze
    hero power (CARDTYPE 10)                hero power <card> [on <target>]
    other button (CARDTYPE 12)              use <card>
    card in hand: minion / spell / other    play / cast / play <card> [on <target>]
    card in play, answer = MOVE_MINION      move <card> / move in shop <card> (a shop card, Bob's side)
    card in play, answer = PLAY block       activate <card>
    ChooseEntities on the hero pick         hero <picked>
    ChooseEntities on another choice        pick <picked> (from <source>)
Combat start is "combat" with the opponent's hero, combat end "shop" (the next prompt after the combat).
A combat block (combatblocks) is "attack" <attacker> [on <defender>], "deaths", or "trigger" <source>.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from .gamestate import GameState
from .proto import decode

TAG_ZONE, TAG_CONTROLLER, TAG_CARDTYPE = 49, 50, 202
ZONE_PLAY, ZONE_HAND = 1, 3
CARDTYPE_HERO, CARDTYPE_MINION, CARDTYPE_SPELL, CARDTYPE_HERO_POWER, CARDTYPE_BUTTON = 3, 4, 5, 10, 12
CARDTYPE_BG_SPELL = 42
BLOCK_PLAY, BLOCK_MOVE_MINION = 7, 12
OPTION_END_TURN = 2
CHOICE_MULLIGAN = 1
BOB = "TB_BaconShopBob"
BLOCK_VERBS = {1: "attack", 6: "deaths"}  # HistoryBlock.Type of a combat block; anything else is a trigger


@dataclass
class Label:
    verb: str
    card: str = ""
    target: str = ""
    arg: str = ""


def label_points(data) -> list[Label]:
    """One Label per data.points entry (data: replay.ReplayData)."""
    from .replay import ALL_OPTIONS, CHOOSE_ENTITIES, CHOOSE_OPTION, ENTITY_CHOICES, POWER_HISTORY

    plan = data.plan
    clicks = [(p.ms, p.type, p.body) for p in data.rec.packets if p.dir == "C2S" and p.type in (CHOOSE_OPTION, CHOOSE_ENTITIES)]
    t0 = next((p.ms for p in data.rec.packets if p.dir == "C2S" and p.type == 168), 0)
    clicks = [(ms - t0, ptype, body) for ms, ptype, body in clicks if ms >= t0]
    # Several clicks can end on the same point (an action without its own answer); the last one names it.
    click_at_point: dict[float, int] = {}
    for k, end in enumerate(data.action_ends):
        click_at_point[end] = k
    combat_starts = {s for s, _ in data.combats}

    gs = GameState()
    options: dict[int, dict] = {}
    choices: dict[int, dict] = {}
    idx = 0

    def apply_until(vt: float, inclusive: bool) -> None:
        nonlocal idx
        while idx < len(plan) and (plan[idx][0] <= vt if inclusive else plan[idx][0] < vt):
            _, ptype, body, _ = plan[idx]
            if ptype == POWER_HISTORY:
                gs.apply_power_history(data.power[idx])
            elif ptype == ALL_OPTIONS:
                o = decode("PegasusGame.AllOptions", body)
                options[o.get("Id", 0)] = o
            elif ptype == ENTITY_CHOICES:
                c = decode("PegasusGame.EntityChoices", body)
                choices[c.get("Id", 0)] = c
            idx += 1

    labels: list[Label] = []
    for point in data.points:
        if point.kind == "combat start":
            apply_until(point.vt, inclusive=True)
            labels.append(Label("combat", _opponent_hero(gs, data, point.vt)))
            continue
        if point.kind == "combat end":
            labels.append(Label("shop"))
            continue
        if point.kind == "reconnect":
            labels.append(Label("reconnect"))
            continue
        if point.kind == "combat block":
            b = data.blocks[point.block]
            labels.append(Label(BLOCK_VERBS.get(b.type, "trigger"), b.source, b.target))
            continue
        k = click_at_point.get(point.vt)
        if k is None:
            labels.append(Label("action"))
            continue
        click_vt, ptype, body = clicks[k]
        apply_until(click_vt, inclusive=False)  # the state the player clicked in
        if ptype == CHOOSE_ENTITIES:
            labels.append(_label_choice(gs, decode("PegasusGame.ChooseEntities", body), choices))
        else:
            labels.append(_label_option(gs, decode("PegasusGame.ChooseOption", body), options,
                                        _answer_block(data, click_vt, point.vt)))
    return labels


def _card(gs: GameState, eid: int | None) -> str:
    e = gs.entities.get(eid or 0)
    return e.card_id if e else ""


def _answer_block(data, click_vt: float, end_vt: float) -> dict | None:
    """First block (PowerStart) the server answered the click with."""
    from .replay import POWER_HISTORY
    for i, (rel, ptype, _, _) in enumerate(data.plan):
        if rel <= click_vt:
            continue
        if rel > end_vt:
            break
        if ptype == POWER_HISTORY:
            for entry in data.power[i].get("List", []):
                if "PowerStart" in entry:
                    return entry["PowerStart"]
    return None


def _label_choice(gs: GameState, chosen: dict, choices: dict[int, dict]) -> Label:
    choice = choices.get(chosen.get("Id", 0), {})
    picked = [_card(gs, e) for e in chosen.get("Entities", [])]
    first = next((c for c in picked if c), "")
    if choice.get("ChoiceType") == CHOICE_MULLIGAN:
        return Label("hero", first)
    return Label("pick", first, _card(gs, choice.get("Source")))


def _label_option(gs: GameState, chosen: dict, options: dict[int, dict], block: dict | None) -> Label:
    opts = options.get(chosen.get("Id", 0), {}).get("Options", [])
    index = chosen.get("Index", 0)
    if index >= len(opts):
        return Label("action")
    option = opts[index]
    if option.get("Type_") == OPTION_END_TURN:
        return Label("end turn")
    main_id = option.get("MainOption", {}).get("Id")
    main = gs.entities.get(main_id or 0)
    if main is None:
        return Label("action")
    card = main.card_id
    target = _card(gs, chosen.get("Target"))
    if card.startswith("TB_BaconShop_DragBuy"):
        return Label("buy", target)
    if card.startswith("TB_BaconShop_DragSell"):
        return Label("sell", target)
    tier = re.match(r"TB_BaconShopTechUp0?(\d+)_Button", card)
    if tier:
        return Label("upgrade", card, arg=tier.group(1))
    if "Reroll" in card:
        return Label("refresh", card)
    if "LockAll" in card or "Freeze" in card:
        return Label("freeze", card)
    cardtype = main.tags.get(TAG_CARDTYPE)
    if cardtype == CARDTYPE_HERO_POWER:
        return Label("hero power", card, target)
    if cardtype == CARDTYPE_BUTTON:
        return Label("use", card, target)
    zone = main.tags.get(TAG_ZONE)
    if zone == ZONE_HAND:
        verb = "play" if cardtype == CARDTYPE_MINION else "cast" if cardtype in (CARDTYPE_SPELL, CARDTYPE_BG_SPELL) else "play"
        return Label(verb, card, target)
    block_type = (block or {}).get("Type")
    if block_type == BLOCK_MOVE_MINION:
        # Shop cards belong to Bob's side: dragging one only reorders the shop.
        friendly = main.tags.get(TAG_CONTROLLER) == _friendly_player_id(gs)
        return Label("move" if friendly else "move in shop", card)
    if block_type == BLOCK_PLAY:
        return Label("activate", card, target)
    return Label("action", card, target)


def _friendly_player_id(gs: GameState) -> int | None:
    """PLAYER_ID of the side whose hand holds cards (Bob's side never has a hand)."""
    for e in gs.entities.values():
        if e.tags.get(TAG_ZONE) == ZONE_HAND and e.tags.get(TAG_CONTROLLER):
            return e.tags[TAG_CONTROLLER]
    return None


def _opponent_hero(gs: GameState, data, vt: float) -> str:
    """The hero the friendly player fights: the non-Bob hero in play on the other side once combat has started."""
    from .replay import POWER_HISTORY
    friendly = gs.entities.get(data.friendly_player_entity or 0)
    friendly_id = friendly.tags.get(30) if friendly else None  # PLAYER_ID
    # The opponent's hero is put into play in the packets right after the combat-phase change and set aside
    # again before the combat's end, so check after every packet of the next few seconds.
    probe = gs.copy()
    for i, (rel, ptype, _, _) in enumerate(data.plan):
        if rel <= vt or ptype != POWER_HISTORY:
            continue
        if rel > vt + 5000:
            break
        probe.apply_power_history(data.power[i])
        for e in probe.entities.values():
            if (e.tags.get(TAG_CARDTYPE) == CARDTYPE_HERO and e.tags.get(TAG_ZONE) == ZONE_PLAY
                    and e.tags.get(TAG_CONTROLLER) != friendly_id and e.card_id and e.card_id != BOB):
                return e.card_id
    return ""
