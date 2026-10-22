"""Client-view game state rebuilt from PowerHistory, and snapshots that restore it (rewind).

GameState.apply_power_history() follows the same entries the client applies (CreateGame, FullEntity,
ShowEntity, ChangeEntity, HideEntity, TagChange, TagListChange, ResetGame). snapshot_power_history() turns the
current state into one PowerHistory packet the client accepts as a reset:

    BLOCK_START type=GAME_RESET  ->  ResetGame{CreateGame, SourceIsRewind}  ->  FullEntity x N  ->  BLOCK_END

GameState.OnResetGame (client) decommissions the game entity, runs CleanGameState() (all entities gone) and
recreates the game entity and players from the CreateGame; the FullEntity entries then recreate everything else.
A GAME_RESET block makes the PowerProcessor drive it with RewindGameSpellController (SourceIsRewind) or
ResetGameSpellController. Whether the FullEntity entries belong inside the block is UNVERIFIED — see docs.
"""
from __future__ import annotations

import copy
from dataclasses import dataclass, field

from .proto import encode

GAME_RESET_BLOCK = 11  # PegasusGame.HistoryBlock.Type.GAME_RESET
TAG_ZONE = 49          # GAME_TAG.ZONE


@dataclass
class Ent:
    id: int
    card_id: str = ""
    tags: dict[int, int] = field(default_factory=dict)
    def_tags: dict[int, int] = field(default_factory=dict)
    tag_lists: dict[int, list[int]] = field(default_factory=dict)
    def_tag_lists: dict[int, list[int]] = field(default_factory=dict)


def _tags(lst: list[dict] | None) -> dict[int, int]:
    return {t.get("Name", 0): t.get("Value", 0) for t in (lst or [])}


def _tag_lists(lst: list[dict] | None) -> dict[int, list[int]]:
    return {t.get("Name", 0): list(t.get("Values", [])) for t in (lst or [])}


def _tag_msgs(tags: dict[int, int]) -> list[dict]:
    return [{"Name": k, "Value": v} for k, v in sorted(tags.items())]


def _tag_list_msgs(lists: dict[int, list[int]]) -> list[dict]:
    return [{"Name": k, "Values": v} for k, v in sorted(lists.items())]


class GameState:
    def __init__(self) -> None:
        self.create_game: dict | None = None  # last CreateGame as decoded (players, infos, uuid, action infos)
        self.game_entity_id: int | None = None
        self.player_entity_ids: list[int] = []
        self.entities: dict[int, Ent] = {}

    def copy(self) -> "GameState":
        return copy.deepcopy(self)

    # ---- applying history ------------------------------------------------------------------------------

    def apply_power_history(self, ph: dict) -> None:
        for data in ph.get("List", []):
            self.apply_data(data)

    def apply_data(self, d: dict) -> None:
        if "CreateGame" in d:
            self._create_game(d["CreateGame"])
        elif "ResetGame" in d:
            self._create_game(d["ResetGame"].get("CreateGame", {}))
        elif "FullEntity" in d:
            e = d["FullEntity"]
            self.entities[e.get("Entity", 0)] = Ent(
                e.get("Entity", 0), e.get("Name", ""), _tags(e.get("Tags")), _tags(e.get("DefTags")),
                _tag_lists(e.get("TagLists")), _tag_lists(e.get("DefTagLists")))
        elif "ShowEntity" in d or "ChangeEntity" in d:
            e = d.get("ShowEntity") or d.get("ChangeEntity")
            ent = self._get(e.get("Entity", 0))
            ent.card_id = e.get("Name", "")
            ent.tags.update(_tags(e.get("Tags")))
            ent.def_tags.update(_tags(e.get("DefTags")))
            ent.tag_lists.update(_tag_lists(e.get("TagLists")))
            ent.def_tag_lists.update(_tag_lists(e.get("DefTagLists")))
        elif "HideEntity" in d:
            h = d["HideEntity"]
            ent = self._get(h.get("Entity", 0))
            ent.tags[TAG_ZONE] = h.get("Zone", 0)
            ent.card_id = ""
        elif "TagChange" in d:
            t = d["TagChange"]
            ent = self._get(t.get("Entity", 0))
            (ent.def_tags if t.get("ChangeDef") else ent.tags)[t.get("Tag", 0)] = t.get("Value", 0)
        elif "TagListChange" in d:
            t = d["TagListChange"]
            ent = self._get(t.get("Entity", 0))
            (ent.def_tag_lists if t.get("ChangeDef") else ent.tag_lists)[t.get("Tag", 0)] = list(t.get("TagList", []))
        # PowerStart/PowerEnd/MetaData/SubSpell*/Vo*/ShuffleDeck/CachedTagForDormantChange carry no board state.

    def _get(self, eid: int) -> Ent:
        ent = self.entities.get(eid)
        if ent is None:
            ent = self.entities[eid] = Ent(eid)
        return ent

    def _create_game(self, cg: dict) -> None:
        self.create_game = copy.deepcopy(cg)
        self.entities.clear()
        ge = cg.get("GameEntity", {})
        self.game_entity_id = ge.get("Id", 0)
        self.entities[self.game_entity_id] = Ent(self.game_entity_id, "", _tags(ge.get("Tags")), {}, _tag_lists(ge.get("TagLists")))
        self.player_entity_ids = []
        for p in cg.get("Players", []):
            pe = p.get("Entity", {})
            pid = pe.get("Id", 0)
            self.player_entity_ids.append(pid)
            self.entities[pid] = Ent(pid, "", _tags(pe.get("Tags")), {}, _tag_lists(pe.get("TagLists")))

    # ---- snapshots ---------------------------------------------------------------------------------------

    def current_create_game(self) -> dict:
        """The last CreateGame with the game and player entities' tags as they are now (a ResetGame's CreateGame)."""
        if self.create_game is None:
            raise ValueError("no CreateGame seen yet")
        cg = copy.deepcopy(self.create_game)
        ge = self.entities[self.game_entity_id]
        cg["GameEntity"] = {"Id": ge.id, "Tags": _tag_msgs(ge.tags), "TagLists": _tag_list_msgs(ge.tag_lists)}
        for p in cg.get("Players", []):
            pid = p.get("Entity", {}).get("Id", 0)
            pe = self.entities.get(pid)
            if pe is not None:
                p["Entity"] = {"Id": pid, "Tags": _tag_msgs(pe.tags), "TagLists": _tag_list_msgs(pe.tag_lists)}
        return cg

    def snapshot_power_history(self, rewind: bool = True, entities_inside_block: bool = True,
                               source: int | None = None) -> bytes:
        """One PowerHistory packet body that makes the client rebuild this exact state.

        `source` is the block's Source entity (default: the game entity). With SourceIsRewind the client picks the
        screen wipe by the source's controller side: FRIENDLY -> RewindSpellScreenWipe, anything else (the game
        entity is NEUTRAL) -> RewindSpellScreenWipeOpponent.
        """
        cg = self.current_create_game()
        special = {self.game_entity_id, *self.player_entity_ids}
        full = [{"FullEntity": {"Entity": e.id, "Name": e.card_id, "Tags": _tag_msgs(e.tags),
                                "DefTags": _tag_msgs(e.def_tags), "TagLists": _tag_list_msgs(e.tag_lists),
                                "DefTagLists": _tag_list_msgs(e.def_tag_lists)}}
                for eid, e in sorted(self.entities.items()) if eid not in special]
        start = {"PowerStart": {"Type": GAME_RESET_BLOCK,
                                 "Source": self.game_entity_id if source is None else source}}
        reset = {"ResetGame": {"CreateGame": cg, "SourceIsRewind": rewind}}
        end = {"PowerEnd": {}}
        items = [start, reset, *full, end] if entities_inside_block else [start, reset, end, *full]
        return encode("PegasusGame.PowerHistory", {"List": items})
