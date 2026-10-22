"""Made-up games for the Replays page: rows in recordings/.catalog.seed.txt, which the catalog lists beside the
real games (hsserver.catalog). For seeing how the page copes with many rows; a seeded game can't be watched.

Cards come from the HS_CardExtractor's latest data export (v2-output/data/<date>: heroes with their hero power,
pool minions with their golden ids, pool trinkets), so every id is a real Battlegrounds card.

    python tools/seed_catalog.py                  # 50 games
    python tools/seed_catalog.py --count 20 --seed 7
    python tools/seed_catalog.py --clear          # remove them
"""
from __future__ import annotations

import argparse
import json
import random
import sys
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver.catalog import SEED_NAME, Game, row  # noqa: E402

EXTRACTOR_DATA = Path(r"E:\PYPY\HS_CardExtractor\v2-output\data")


def latest_export(root: Path) -> Path:
    dated = sorted(p for p in root.iterdir() if p.is_dir() and p.name[:4].isdigit()
                   and (p / "_render_manifest.json").is_file())
    if not dated:
        sys.exit(f"no card data export under {root}")
    return dated[-1]


def pools(folder: Path):
    def cards(name):
        return json.loads((folder / f"{name}.json").read_text(encoding="utf-8"))["cards"]

    card_id = {m["id"]: m for m in json.loads((folder / "_render_manifest.json").read_text(encoding="utf-8"))}
    powers = {c["id"] for c in cards("hero_powers")}
    heroes = [(card_id[c["id"]]["cardId"], card_id[c["childIds"][0]]["cardId"])
              for c in cards("heroes")
              if c["pool"] and not c["isHeroSkin"] and not c["isDuosOnly"] and c["childIds"]
              and c["childIds"][0] in powers and c["id"] in card_id and c["childIds"][0] in card_id]
    minions = []  # (card id, golden card id, tier, attack, health)
    for c in cards("minions"):
        m = card_id.get(c["id"])
        if not (c["pool"] and c.get("tier") and m) or "_Buddy" in m["cardId"]:
            continue
        gold = card_id.get(m.get("battlegroundsPremiumDbfId") or 0)
        minions.append((m["cardId"], gold["cardId"] if gold else None, c["tier"], c["attack"], c["health"]))
    trinkets = {"lesser": [], "greater": []}
    for c in cards("trinkets"):
        m = card_id.get(c["id"])
        if c["pool"] and m and not m["cardId"].endswith("t") and c.get("trinketTier") in trinkets:
            trinkets[c["trinketTier"]].append(m["cardId"])
    return heroes, minions, trinkets


def make_game(rng: random.Random, when: datetime, n: int, heroes, minions, trinkets) -> Game:
    place = rng.randint(1, 8)
    # Earlier exits play fewer turns; a turn is about two minutes.
    turns = max(6, min(22, round(rng.gauss(17 - place, 2))))
    minutes = max(8, round(turns * rng.uniform(1.8, 2.4)))
    hero, power = rng.choice(heroes)
    hero_powers = [power]
    if rng.random() < 0.15:
        hero_powers.append(rng.choice(heroes)[1])
    picked = []
    if turns >= 6:
        picked.append(rng.choice(trinkets["lesser"]))
    if turns >= 9:
        picked.append(rng.choice(trinkets["greater"]))
    top_tier = min(7, 1 + turns // 2)
    in_reach = [m for m in minions if m[2] <= top_tier and m[2] >= top_tier - 2] or minions
    size = 7 if rng.random() < 0.7 else rng.randint(3, 6)
    growth = 1 + (turns / 6) ** 2.2 * rng.uniform(0.6, 1.6)  # late boards are big
    board = []
    for _ in range(size):
        card, gold, _tier, attack, health = rng.choice(in_reach)
        golden = gold is not None and rng.random() < 0.3
        mult = growth * (2 if golden else 1) * rng.uniform(0.7, 1.3)
        board.append([gold if golden else card, max(1, round(attack * mult)), max(1, round(health * mult)), int(golden)])
    return Game(id=when.strftime("%Y%m%d_%H%M%S") + f"_seed{n:02d}", start=when.strftime("%Y-%m-%d %H:%M"),
                hero=hero, place=place, turns=turns, minutes=minutes, rating=None, change=None,
                board=board, hero_powers=hero_powers, trinkets=picked)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--folder", default="recordings", help="the recordings folder (default: recordings)")
    ap.add_argument("--count", type=int, default=50)
    ap.add_argument("--seed", type=int, default=1, help="random seed (the same seed gives the same games)")
    ap.add_argument("--cards", type=Path, help=f"a card data export (default: the latest under {EXTRACTOR_DATA})")
    ap.add_argument("--clear", action="store_true", help="remove the seeded games")
    args = ap.parse_args()
    out = Path(args.folder) / SEED_NAME
    if args.clear:
        out.unlink(missing_ok=True)
        print(f"removed {out}; the page drops the games at its next scan")
        return
    data = args.cards or latest_export(EXTRACTOR_DATA)
    heroes, minions, trinkets = pools(data)
    rng = random.Random(args.seed)
    # From a week ago backwards (below the real games), a few games a day.
    when = datetime.now().replace(hour=23, minute=0, second=0, microsecond=0) - timedelta(days=7)
    games = []
    for n in range(args.count):
        when -= timedelta(minutes=rng.randint(45, 600))
        games.append(make_game(rng, when, n, heroes, minions, trinkets))
    out.write_text("\n".join(row(g) for g in games) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(games)} games -> {out} (cards from {data}: {len(heroes)} heroes, {len(minions)} minions, "
          f"{sum(map(len, trinkets.values()))} trinkets)")


if __name__ == "__main__":
    main()
