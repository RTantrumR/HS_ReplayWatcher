"""Holds the plugin's text catalogs (plugin/lang/<code>.json) to the key source (enUS.json) and to the code.

Checks: enUS.json is flat string→string with dotted camelCase keys; every key the C# references exists in enUS.json
and every enUS key is referenced (dynamic ones, place.N, by their prefix); every other catalog has no key English
lacks, the same {n} placeholders per key, |4(...) groups with as many forms as its language has (or none: one form),
and lists the keys it lacks (they render English) and the values left identical to English (for review).
Game-string references (@KEY) are checked against the installed client's string tables when they are there.

Usage: python tools/check_strings.py [--strict]   (--strict: a missing key fails, the gate before a release)
       python tools/check_strings.py --pseudo     (writes lang/xxXX.json, the lengthened accented pseudo-locale)
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LANG = ROOT / "plugin" / "lang"
PLUGIN = ROOT / "plugin"
STRINGS_DIRS = [Path(r"E:\Games\Hearthstone\Strings")]
PLURAL_FORMS = {"plPL": 3}  # every other catalog language: 2 (Locales.cs mirrors the client's GetPluralIndex)
DYNAMIC_PREFIXES = ("place.",)  # keys the code builds at run time

KEY_RE = re.compile(r"^[a-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$")
PLACEHOLDER_RE = re.compile(r"\{(\d+)\}")
PLURAL_RE = re.compile(r"\|4\(([^)]*)\)")
LITERAL_RE = re.compile(r'"([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)"')


def load(path: Path) -> dict[str, str]:
    raw = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("not an object")
    for k, v in raw.items():
        if not isinstance(v, str):
            raise ValueError(f"{k!r} is not a string")
    return raw


def placeholders(s: str) -> str:
    return ",".join(sorted(set(PLACEHOLDER_RE.findall(s))))


def game_keys_available() -> dict[str, set[str]] | None:
    """The installed client's string keys per locale, or None when no install is at hand."""
    for d in STRINGS_DIRS:
        if not d.is_dir():
            continue
        out: dict[str, set[str]] = {}
        for loc in sorted(p.name for p in d.iterdir() if p.is_dir()):
            keys: set[str] = set()
            for f in (d / loc).glob("*.txt"):
                for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
                    if "\t" in line:
                        keys.add(line.split("\t", 1)[0])
            out[loc] = keys
        return out
    return None


def pseudo(en: dict[str, str]) -> dict[str, str]:
    """Every English value lengthened by ~40 % and accented, placeholders, |4 and @ references kept."""
    table = str.maketrans("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPRSTUVWYZ",
                          "åƀçðéƒğĥíĵķļmñöþqŕşţûvŵxýžÅƁÇÐÉƑĞĤÍĴĶĻMÑÖÞŔŞŢÛVŴÝŽ")
    out = {}
    for k, v in en.items():
        if v.startswith("@") or k == "row.date" or k == "import.dialog.filter":
            out[k] = v
            continue
        parts = re.split(r"(\{\d+\}|\|4\([^)]*\))", v)
        text = "".join(p if (p.startswith("{") or p.startswith("|4(")) else p.translate(table) for p in parts)
        extra = max(1, round(len(v) * 0.4 / 2))
        out[k] = "[" + text + " " + "~" * extra + "]"
    return out


def main() -> int:
    strict = "--strict" in sys.argv
    failed = False
    en_path = LANG / "enUS.json"
    en = load(en_path)
    print(f"enUS.json: {len(en)} keys")
    bad = [k for k in en if not KEY_RE.match(k)]
    if bad:
        print(f"  enUS: keys not dotted camelCase: {', '.join(bad[:10])}")
        failed = True
    for k, v in en.items():
        if not v.strip():
            print(f"  enUS: {k} is empty")
            failed = True

    if "--pseudo" in sys.argv:
        (LANG / "xxXX.json").write_text(json.dumps(pseudo(en), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("wrote lang/xxXX.json (never shipped: the csproj embeds only the real catalogs)")

    # The code's keys: every dotted literal in plugin/*.cs that is or could be a key.
    used: set[str] = set()
    for cs in glob.glob(str(PLUGIN / "**" / "*.cs"), recursive=True):
        for m in LITERAL_RE.finditer(Path(cs).read_text(encoding="utf-8", errors="replace")):
            used.add(m.group(1))
    missing_in_en = sorted(k for k in used if k not in en and not k.startswith(DYNAMIC_PREFIXES)
                           and k.count(".") >= 1 and k.split(".")[0] in {s.split(".")[0] for s in en})
    unused = sorted(k for k in en if k not in used and not k.startswith(DYNAMIC_PREFIXES))
    if missing_in_en:
        print(f"  code references keys enUS lacks: {', '.join(missing_in_en)}")
        failed = True
    if unused:
        print(f"  enUS keys no code references: {', '.join(unused)}")
        failed = True

    game = game_keys_available()
    refs = {k: v[1:] for k, v in en.items() if v.startswith("@")}
    if refs and game:
        for k, gk in refs.items():
            lacking = [loc for loc, keys in game.items() if gk not in keys]
            if lacking:
                print(f"  @{gk} ({k}) is missing from the client's tables: {', '.join(lacking)}")
                failed = True
    elif refs:
        print("  (no client install found: @ references not checked)")

    for path in sorted(LANG.glob("*.json")):
        code = path.stem
        if code == "enUS" or code == "xxXX":
            continue
        try:
            cat = load(path)
        except Exception as e:  # noqa: BLE001
            print(f"{code}: {e}")
            failed = True
            continue
        forms = PLURAL_FORMS.get(code, 2)
        unknown = [k for k in cat if k not in en]
        missing = [k for k in en if k not in cat]
        bad_ph = [k for k in cat if k in en and placeholders(cat[k]) != placeholders(en[k])]
        empty = [k for k in cat if not cat[k].strip()]
        bad_plural = [k for k in cat if any(len(g.split(",")) != forms for g in PLURAL_RE.findall(cat[k]))]
        same = [k for k in cat if k in en and cat[k] == en[k] and re.search(r"[A-Za-z]{4,}", en[k])
                and not en[k].startswith("@")]
        print(f"{code}: {len(cat)} keys, {len(missing)} missing, {len(same)} same as English")
        if unknown:
            print(f"  {code}: unknown keys: {', '.join(unknown)}")
            failed = True
        if bad_ph:
            print(f"  {code}: placeholder mismatch: {', '.join(bad_ph)}")
            failed = True
        if empty:
            print(f"  {code}: empty values: {', '.join(empty)}")
            failed = True
        if bad_plural:
            print(f"  {code}: |4 groups without {forms} forms: {', '.join(bad_plural)}")
            failed = True
        if missing and strict:
            print(f"  {code}: missing: {', '.join(missing)}")
            failed = True
        elif missing:
            print(f"  {code}: missing (render English): {', '.join(missing[:8])}{', …' if len(missing) > 8 else ''}")
        if same:
            print(f"  {code}: same as English: {', '.join(same[:12])}{', …' if len(same) > 12 else ''}")

    print("strings: FAILED" if failed else "strings: OK")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
