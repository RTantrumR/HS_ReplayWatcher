"""The screenshot tour's driver: starts the plugin's tour in the running client, waits for it, and tiles each surface
across the languages into contact sheets, so text running out of its box, clipped or unwrapped shows at a glance.

The client must be running with the plugin (the tour waits for the hub itself, then goes to the BG lobby and the
Replays page by code; no mouse or keyboard). Shots land in %LOCALAPPDATA%\\HS Replay Watcher\\Shots\\<run>\\<locale>\\.

    python tools/tour.py                       every language, the page and the bar
    python tools/tour.py xxXX deDE page        some languages, one pass
    python tools/tour.py --sheets <run folder> only the sheets, for a run already shot

Sheets go to <run>\\sheets\\<surface>_<n>.png, four languages each (2 x 2), cropped to the surface's region.
Needs Pillow.
"""
from __future__ import annotations

import os
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

SHOTS = Path(os.environ.get("LOCALAPPDATA", "")) / "HS Replay Watcher" / "Shots"
ORDER = ["xxXX", "enUS", "deDE", "frFR", "esES", "esMX", "itIT", "ptBR", "plPL", "jaJP", "koKR", "zhCN", "zhTW", "thTH"]
PER_SHEET = 4

# A surface's region at 1920x1080 (x0, y0, x1, y1): the part worth comparing. Bar shots are the top 32 % only.
POPUP = (480, 250, 1440, 830)
CROPS = {
    "01-recent": (340, 0, 1580, 560),
    "02-status": (340, 170, 1580, 270),
    "03-saved": (340, 0, 1580, 300),
    "04-sort-menu": (340, 120, 1100, 400),
    "05-empty-saved": (340, 120, 1580, 200),
    "06-imported": (340, 120, 1580, 300),
    "07-empty-imported": (340, 120, 1580, 200),
    "08-settings": (400, 100, 1520, 880),
    "08b-settings-tip": (400, 100, 1520, 880),
    "17-lobby": (1300, 250, 1640, 500),
}
BAR = (160, 0, 1820, 345)


def crop_for(surface: str, size: tuple[int, int]) -> tuple[int, int, int, int]:
    box = CROPS.get(surface) or (BAR if surface[:2] == "e-" or (surface[:1] in "bg" and surface[1:2].isdigit()) else POPUP)
    s = size[0] / 1920  # a bar shot is the top of the frame, so its rows are the frame's rows
    x0, y0, x1, y1 = box
    return (round(x0 * s), round(y0 * s), min(size[0], round(x1 * s)), min(size[1], round(y1 * s)))


def sheets(run: Path) -> list[Path]:
    locales = [l for l in ORDER if (run / l).is_dir()] + sorted(p.name for p in run.iterdir()
                                                               if p.is_dir() and p.name not in ORDER and p.name != "sheets")
    surfaces = sorted({p.stem for l in locales for p in (run / l).glob("*.png")})
    out_dir = run / "sheets"
    out_dir.mkdir(exist_ok=True)
    try:
        font = ImageFont.truetype("arial.ttf", 22)
    except OSError:
        font = ImageFont.load_default()
    written = []
    for surface in surfaces:
        have = [l for l in locales if (run / l / f"{surface}.png").exists()]
        for n in range(0, len(have), PER_SHEET):
            group = have[n:n + PER_SHEET]
            tiles = []
            for l in group:
                im = Image.open(run / l / f"{surface}.png").convert("RGB")
                tiles.append((l, im.crop(crop_for(surface, im.size))))
            tw = max(t.width for _, t in tiles)
            th = max(t.height for _, t in tiles) + 30
            cols = 2 if len(tiles) > 1 else 1
            rows = (len(tiles) + cols - 1) // cols
            sheet = Image.new("RGB", (tw * cols, th * rows), (24, 24, 24))
            draw = ImageDraw.Draw(sheet)
            for i, (l, t) in enumerate(tiles):
                x, y = (i % cols) * tw, (i // cols) * th
                draw.text((x + 8, y + 4), l, fill=(255, 220, 120), font=font)
                sheet.paste(t, (x, y + 30))
            path = out_dir / f"{surface}_{n // PER_SHEET + 1}.png"
            sheet.save(path)
            written.append(path)
    return written


def main() -> int:
    args = sys.argv[1:]
    if args[:1] == ["--sheets"]:
        run = Path(args[1])
        print(f"{len(sheets(run))} sheets in {run / 'sheets'}")
        return 0
    SHOTS.mkdir(parents=True, exist_ok=True)
    before = {p.name for p in SHOTS.iterdir() if p.is_dir()}
    (SHOTS / "tour.txt").write_text(" ".join(args), encoding="utf-8")
    print("tour asked for:", " ".join(args) or "everything", "- waiting for the client")
    started = time.time()
    run = None
    while time.time() - started < 3600:
        time.sleep(5)
        new = [p for p in SHOTS.iterdir() if p.is_dir() and p.name not in before]
        if new:
            run = max(new, key=lambda p: p.name)
            if (run / "done.txt").exists():
                break
    if run is None or not (run / "done.txt").exists():
        print("the tour didn't finish within an hour (is the client running with the plugin?)")
        return 1
    shots = (run / "done.txt").read_text(encoding="utf-8").split()
    print(f"{len(shots)} shots in {run}")
    print(f"{len(sheets(run))} sheets in {run / 'sheets'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
