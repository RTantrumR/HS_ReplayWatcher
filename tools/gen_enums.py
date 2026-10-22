"""Generate hsserver/enums.json: the client enums Power.log prints by name (hsserver/powerlog.py reads it).

Power.log prints tags, enum-valued tags, block types, meta types, play errors and choice types as C# enum names
(Tags.DebugTag, GameState.DebugPrintPower), so turning a log back into packets needs the client's own tables.
They come from a decompile of the installed client (ilspycmd 9.x, a global dotnet tool):

    ilspycmd -p -o <out>\\Assembly-CSharp -r <HS>\\Hearthstone_Data\\Managed <HS>\\Hearthstone_Data\\Managed\\Assembly-CSharp.dll
    ilspycmd -p -o <out>\\blizzard.bgsclient -r <HS>\\Hearthstone_Data\\Managed <HS>\\Hearthstone_Data\\Managed\\blizzard.bgsclient.dll
    python tools/gen_enums.py <out>

Regenerate after a client patch (a new tag name in a log fails the conversion by name, which is the signal).
Read like tools/gen_schema.py: regexes over the .cs files, no C# parser. Members without a value count up from
the previous one (C# rules); the file records the client's build number from GameState.DebugPrintGame.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

# json key -> (file under the dump root, enum name in that file)
ENUMS = {
    "GAME_TAG": ("Assembly-CSharp/GAME_TAG.cs", "GAME_TAG"),
    "TAG_STATE": ("Assembly-CSharp/TAG_STATE.cs", "TAG_STATE"),
    "TAG_ZONE": ("Assembly-CSharp/TAG_ZONE.cs", "TAG_ZONE"),
    "TAG_STEP": ("Assembly-CSharp/TAG_STEP.cs", "TAG_STEP"),
    "TAG_PLAYSTATE": ("Assembly-CSharp/TAG_PLAYSTATE.cs", "TAG_PLAYSTATE"),
    "TAG_CARDTYPE": ("Assembly-CSharp/TAG_CARDTYPE.cs", "TAG_CARDTYPE"),
    "TAG_MULLIGAN": ("Assembly-CSharp/TAG_MULLIGAN.cs", "TAG_MULLIGAN"),
    "TAG_CLASS": ("Assembly-CSharp/TAG_CLASS.cs", "TAG_CLASS"),
    "TAG_FACTION": ("Assembly-CSharp/TAG_FACTION.cs", "TAG_FACTION"),
    "TAG_RACE": ("Assembly-CSharp/TAG_RACE.cs", "TAG_RACE"),
    "TAG_RARITY": ("Assembly-CSharp/TAG_RARITY.cs", "TAG_RARITY"),
    "TAG_ENCHANTMENT_VISUAL": ("Assembly-CSharp/TAG_ENCHANTMENT_VISUAL.cs", "TAG_ENCHANTMENT_VISUAL"),
    "TAG_CARD_SET": ("Assembly-CSharp/TAG_CARD_SET.cs", "TAG_CARD_SET"),
    "CHOICE_TYPE": ("Assembly-CSharp/CHOICE_TYPE.cs", "CHOICE_TYPE"),
    "PlayErrors.ErrorType": ("Assembly-CSharp/PlayErrors.cs", "ErrorType"),
    "HistoryBlock.Type": ("blizzard.bgsclient/PegasusGame/HistoryBlock.cs", "Type"),
    "HistoryMeta.Type": ("blizzard.bgsclient/PegasusGame/HistoryMeta.cs", "Type"),
    "GameType": ("blizzard.bgsclient/PegasusShared/GameType.cs", "GameType"),
    "FormatType": ("blizzard.bgsclient/PegasusShared/FormatType.cs", "FormatType"),
}

# GAME_TAG name -> the enum its value is printed with (Tags.DebugTag; every other tag prints a number).
VALUE_ENUMS = {
    "STATE": "TAG_STATE", "ZONE": "TAG_ZONE", "STEP": "TAG_STEP", "NEXT_STEP": "TAG_STEP",
    "PLAYSTATE": "TAG_PLAYSTATE", "CARDTYPE": "TAG_CARDTYPE", "MULLIGAN_STATE": "TAG_MULLIGAN",
    "CLASS": "TAG_CLASS", "FACTION": "TAG_FACTION", "CARDRACE": "TAG_RACE", "RARITY": "TAG_RARITY",
    "ENCHANTMENT_BIRTH_VISUAL": "TAG_ENCHANTMENT_VISUAL", "ENCHANTMENT_IDLE_VISUAL": "TAG_ENCHANTMENT_VISUAL",
    "CARD_SET": "TAG_CARD_SET",
}

# Numbers the server hardcodes; the generated table must agree with them.
EXPECTED = {("GAME_TAG", "ZONE"): 49, ("GAME_TAG", "STEP"): 19, ("GAME_TAG", "CARDTYPE"): 202,
            ("GAME_TAG", "PLAYSTATE"): 17, ("GAME_TAG", "BACON_IN_COMBAT_PHASE"): 1522, ("GAME_TAG", "TURN"): 20,
            ("GAME_TAG", "DEFENDING"): 36, ("TAG_ZONE", "PLAY"): 1, ("HistoryBlock.Type", "ATTACK"): 1,
            ("HistoryBlock.Type", "DEATHS"): 6, ("HistoryBlock.Type", "GAME_RESET"): 11}

MEMBER_RE = re.compile(r"^\s*(\w+)(?:\s*=\s*(-?\d+))?\s*,?\s*$")


def parse_enum(text: str, name: str) -> dict[str, int]:
    m = re.search(r"\benum " + re.escape(name) + r"\b[^{]*\{(.*?)\n\s*\}", text, re.S)
    if not m:
        raise ValueError(f"enum {name} not found")
    members: dict[str, int] = {}
    value = -1
    for line in m.group(1).splitlines():
        line = line.split("//")[0]
        if not line.strip():
            continue
        mm = MEMBER_RE.match(line)
        if not mm:
            raise ValueError(f"enum {name}: unreadable member line {line!r}")
        value = int(mm.group(2)) if mm.group(2) is not None else value + 1
        members[mm.group(1)] = value
    return members


def main() -> None:
    root = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parents[1] / "hsserver" / "enums.json"
    enums = {key: parse_enum((root / rel).read_text(encoding="utf-8", errors="replace"), name)
             for key, (rel, name) in ENUMS.items()}
    for (enum, member), number in EXPECTED.items():
        if enums[enum].get(member) != number:
            raise SystemExit(f"{enum}.{member} = {enums[enum].get(member)}, the server expects {number}")
    for tag, enum in VALUE_ENUMS.items():
        if tag not in enums["GAME_TAG"] or enum not in enums:
            raise SystemExit(f"value enum {tag} -> {enum}: unknown")
    game_state = (root / "Assembly-CSharp/GameState.cs").read_text(encoding="utf-8", errors="replace")
    build_m = re.search(r"BuildNumber=\{(\d+)\}", game_state)
    build = int(build_m.group(1)) if build_m else 0
    out.write_text(json.dumps({"build": build, "enums": enums, "value_enums": VALUE_ENUMS}, indent=1, sort_keys=True),
                   encoding="utf-8")
    print(f"build {build}: " + ", ".join(f"{k} {len(v)}" for k, v in enums.items()) + f" -> {out}")


if __name__ == "__main__":
    main()
