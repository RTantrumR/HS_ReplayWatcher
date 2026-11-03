"""The game's own words for the terms the plugin uses, in every installed language: the glossary the catalogs follow.

Reads the client's string tables (E:\\Games\\Hearthstone\\Strings\\<locale>\\*.txt, tab-separated KEY<TAB>text[<TAB>note])
for every locale folder there (13 on this install; ruRU ships none).

    python tools/glossary.py KEY [KEY...]        the keys' texts per locale (to judge an @KEY reference)
    python tools/glossary.py --find "Tavern Tier"  every key whose English text is that phrase, per locale
    python tools/glossary.py --md > docs/localization-glossary.md   the plugin's term list as a Markdown table
"""
from __future__ import annotations

import io
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")  # a cp1251 console
from pathlib import Path

STRINGS = Path(r"E:\Games\Hearthstone\Strings")
LOCALES = ["enUS", "deDE", "frFR", "esES", "esMX", "itIT", "ptBR", "plPL", "jaJP", "koKR", "zhCN", "zhTW", "thTH"]

# The terms the plugin's texts use, as (label, the game key that carries the term in its plain sense).
TERMS = [
    ("Battlegrounds", "GLOBAL_BATTLEGROUNDS"),
    ("Tavern Tier", "GAMEPLAY_BACON_TAVERN_TIER"),
    ("Hero Power", "GLOBAL_TUTORIAL_HERO_POWER"),
    ("Hero", "GLOBAL_CARDTYPE_HERO"),
    ("Minion", "GLOBAL_CARDTYPE_MINION"),
    ("Spell", "GLOBAL_CARDTYPE_SPELL"),
    ("Freeze (BG)", "GLOBAL_KEYWORD_BACON_FREEZE"),
    ("Refresh", "GLOBAL_REFRESH"),
    ("Refresh (keyword text)", "GLOBAL_KEYWORD_REFRESH_TEXT"),
    ("Buy", "GAMEPLAY_BACON_DRAGBUY_BUTTON_TUTORIAL"),
    ("Sell", "GAMEPLAY_BACON_DRAGSELL_BUTTON_TUTORIAL"),
    ("Upgrade", "GLUE_BUTTON_UPGRADE"),
    ("Combat", "GAMEPLAY_BACON_COMBAT_PHASE_NAME"),
    ("End turn", "GAMEPLAY_END_TURN"),
    ("Attack", "GLOBAL_ATTACK"),
    ("Spectate", "GLOBAL_SPECTATE"),
    ("Reconnected", "GLOBAL_RECONNECT_RECONNECTED_HEADER"),
    ("Reconnecting", "GLOBAL_RECONNECT_RECONNECTING_HEADER"),
    ("Cancel", "GLOBAL_CANCEL"),
    ("Done", "GLOBAL_DONE"),
    ("Delete", "GLUE_COLLECTION_DECK_DELETE"),
    ("Remove (friend)", "GLOBAL_FRIENDLIST_REMOVE_FRIEND_BUTTON"),
    ("Keep", "GLUE_BUTTON_KEEP"),
    ("Settings", "GLUE_LOADINGSCREEN_BUTTON_SETTINGS"),
    ("Options", "GLOBAL_OPTIONS"),
    ("Loading...", "GLUE_LOADING"),
    ("Play", "GLOBAL_PLAY"),
    ("Back", "GLOBAL_BACK"),
    ("Confirm", "GLOBAL_CONFIRM"),
    # Places: the BG end-of-game banner (1st-4th carry exclamation marks there; the plugin's places don't).
    *[(f"Place {n} (end of game)", f"GAMEPLAY_END_OF_GAME_PLACE_{n}") for n in range(1, 9)],
    ("Best place (mulligan)", "GAMEPLAY_MULLIGAN_BEST_PLACE"),
    ("1st Place label (trap: Polish says 'wins')", "GLUE_BACON_FIRST_PLACE_LABEL"),
    ("Turn (plural)", "GAMEPLAY_TOOLTIP_BACON_DARKMOON_PRIZES_DESC"),
    ("Minutes (plural)", "GLOBAL_DATETIME_AFK_MINUTES"),
    ("Minutes ago (plural)", "GLOBAL_DATETIME_FRIENDREQUEST_MINUTES"),
    ("Spectator Mode", "GLOBAL_SPECTATOR_MODE_INDICATOR_TEXT"),
    ("Reconnecting (sentence)", "GLOBAL_RECONNECT_RECONNECTING"),
    ("Screenshot saved to folder", "GLOBAL_SCREENSHOT_COMPLETE_SPECIFIC_DIRECTORY"),
    ("Folder (error)", "GLOBAL_ERROR_ASSET_CREATE_PERSISTENT_DATA_PATH"),
    ("Remove (spectator)", "GLOBAL_SPECTATOR_KICK_PROMPT_HEADER"),
    ("Are you sure? (the address form)", "GLUE_COLLECTION_DECK_TEMPLATE_REPLACE_MESSAGE"),
    ("Paste (deck code)", "GLUE_COLLECTION_DECK_PASTE"),
    ("Copy", "GLUE_COLLECTION_DECK_COPY"),
]


def tables() -> dict[str, dict[str, str]]:
    out: dict[str, dict[str, str]] = {}
    for loc in LOCALES:
        folder = STRINGS / loc
        if not folder.is_dir():
            continue
        table: dict[str, str] = {}
        for f in sorted(folder.glob("*.txt")):
            for line in f.read_text(encoding="utf-8-sig", errors="replace").splitlines():
                parts = line.rstrip("\r").split("\t")
                if len(parts) >= 2 and parts[0] and not parts[0].startswith("#"):
                    table.setdefault(parts[0], parts[1])
        out[loc] = table
    return out


def show(keys: list[str], t: dict[str, dict[str, str]]) -> None:
    for k in keys:
        print(k)
        for loc, table in t.items():
            print(f"  {loc}: {table.get(k, '(missing)')}")


def main() -> int:
    args = sys.argv[1:]
    t = tables()
    if not t:
        print(f"no string tables under {STRINGS}", file=sys.stderr)
        return 1
    if args and args[0] == "--find":
        phrase = " ".join(args[1:]).strip().lower()
        keys = [k for k, v in t["enUS"].items() if v.strip().lower() == phrase]
        if not keys:
            print(f"no English string is exactly {phrase!r}")
        show(keys, t)
        return 0
    if args and args[0] == "--md":
        locs = list(t)
        print("# The game's words for the plugin's terms\n")
        print("Read from the client's own string tables (`Strings\\<locale>\\*.txt`), so the catalogs use Blizzard's")
        print("player-facing words. Regenerate with `python tools/glossary.py --md > docs/localization-glossary.md`.\n")
        print("| term | key | " + " | ".join(locs) + " |")
        print("|---|---|" + "---|" * len(locs))
        for label, key in TERMS:
            cells = [t[loc].get(key, "—").replace("|", "\\|").replace("\n", " ") for loc in locs]
            print(f"| {label} | `{key}` | " + " | ".join(cells) + " |")
        return 0
    if not args:
        args = [k for _, k in TERMS]
    show(args, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())
