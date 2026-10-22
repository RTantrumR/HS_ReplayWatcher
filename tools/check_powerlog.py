"""Check the Power.log reader against a recording of the same game.

    python tools/check_powerlog.py <Power.log>... <recording.jsonl(.gz)> [--out DIR]

The client writes a Power.log while it watches one of our replays, so a recording and the log of watching it
are the same game seen twice. The log is converted in memory (hsserver/powerlog.py) and its PowerHistory stream
is compared with the recording's entry by entry, after dropping what the log cannot carry (DefTags, EffectCardId,
history flags, PlayerInfos, GameUuid). Packets are compared in step up to the log's first RESET_GAME (a replay
session that jumped has snapshots the recording hasn't); after that each log packet looks for an identical
recording packet. Choice and option packets are compared by id. The converted recording is then written to
--out (default: a temp folder) and loaded the way the server loads it, and the block notes derived from the
log's executed pass are matched to the combats' blocks as the server would match a recorder's notes.
"""
from __future__ import annotations

import argparse
import copy
import json
import logging
import sys
import tempfile
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from hsserver import powerlog  # noqa: E402
from hsserver.proto import decode  # noqa: E402
from hsserver.recording import load  # noqa: E402
from hsserver.replay import ReplayData  # noqa: E402

DROP_START = {"EffectCardId", "ShowInHistory", "IsDeferrable", "IsBatchable", "IsDeferBlocker", "ForceShowBigCard"}
DROP_ENTITY = {"DefTags", "DefTagLists", "TagLists"}
DROP_CREATE = {"PlayerInfos", "GameUuid", "ActionInfos"}  # PlayerInfos: rebuilt with zero ids (fill_player_infos)


def norm_entry(e: dict) -> dict:
    e = copy.deepcopy(e)
    if "PowerStart" in e:
        ps = e["PowerStart"]
        for k in DROP_START:
            ps.pop(k, None)
        if ps.get("TriggerKeyword") == 0:
            ps.pop("TriggerKeyword")
        if ps.get("EffectIndex") == 0 and ps.get("Type") in (6, 7, 11):
            ps.pop("EffectIndex")
    for key in ("FullEntity", "ShowEntity", "ChangeEntity"):
        if key in e:
            for k in DROP_ENTITY:
                e[key].pop(k, None)
    if "CreateGame" in e:
        cg = e["CreateGame"]
        for k in DROP_CREATE:
            cg.pop(k, None)
        for p in cg.get("Players", []):
            p.pop("BnetAccountId", None)
            p.pop("CardBack", None)  # not printed (the log's Player line has ids only)
    if "ResetGame" in e:
        e["ResetGame"] = {"ResetGame": True}  # its CreateGame is rebuilt from state, not read
    if "SubSpellStart" in e and not e["SubSpellStart"].get("TargetEntityIds"):
        e["SubSpellStart"].pop("TargetEntityIds", None)
    if "MetaData" in e:
        md = e["MetaData"]
        if md.get("Data") == 0:
            md.pop("Data")
        if md.get("Type") == 0:
            md.pop("Type")
        md.setdefault("Info", [])
    return e


def norm_packet(entries: list[dict]) -> list[dict]:
    return [norm_entry(e) for e in entries]


def joined_run(entries: list[dict], plan: list[list[dict]], first_of: dict[str, list[int]]) -> tuple[int, int] | None:
    """(first, last) plan packets whose entries, joined, are exactly these; None if no such run."""
    if not entries:
        return None
    for start in first_of.get(json.dumps(entries[0], sort_keys=True), []):
        pos, i = 0, start
        while i < len(plan) and pos < len(entries) and entries[pos:pos + len(plan[i])] == plan[i]:
            pos += len(plan[i])
            i += 1
        if pos == len(entries) and i > start + 1:
            return start, i - 1
    return None


def norm_options(o: dict) -> dict:
    o = copy.deepcopy(o)
    for opt in o.get("Options", []):
        for so in [opt.get("MainOption")] + opt.get("SubOptions", []):
            if so:
                so.pop("PowerKeyword", None)
                so.setdefault("Targets", [])
    return o


def diff(a, b, path="") -> str | None:
    """The first difference between two decoded values, as a path, or None."""
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k not in a:
                return f"{path}.{k} only in recording: {b[k]!r}"[:200]
            if k not in b:
                return f"{path}.{k} only in log: {a[k]!r}"[:200]
            d = diff(a[k], b[k], f"{path}.{k}")
            if d:
                return d
        return None
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return f"{path}: {len(a)} items in log, {len(b)} in recording"
        for i, (x, y) in enumerate(zip(a, b)):
            d = diff(x, y, f"{path}[{i}]")
            if d:
                return d
        return None
    if a != b:
        return f"{path}: log {a!r} != recording {b!r}"
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+", type=Path, help="Power.log file(s) in order, then the recording")
    ap.add_argument("--out", type=Path, help="where to write the converted recording (default: a temp folder)")
    ap.add_argument("--show", type=int, default=12, help="how many differing packets to print")
    args = ap.parse_args()
    logging.basicConfig(level=logging.WARNING)
    *logs, rec_path = args.paths
    rec = load(rec_path)
    rec_power = [(p.ms, decode("PegasusGame.PowerHistory", p.body).get("List", [])) for p in rec.packets
                 if p.dir == "S2C" and p.type == powerlog.POWER_HISTORY]
    # What a watching client was actually sent: the server cuts fight packets into one per visible block.
    rec_plan = [(rel, decode("PegasusGame.PowerHistory", body).get("List", []))
                for rel, ptype, body, _ in ReplayData(rec).plan if ptype == powerlog.POWER_HISTORY]
    rec_seed = next((t["Value"] for t in rec_power[0][1][0]["CreateGame"]["GameEntity"]["Tags"] if t["Name"] == 2042), 0)
    games, parser = powerlog.parse(logs)
    game = next((g for g in games if g.seed == rec_seed), None)
    if game is None:
        print(f"no game with seed {rec_seed} in the log (seeds: {[g.seed for g in games]})")
        return 2
    print(f"log game: {powerlog.summary(game)}")
    print(f"recording: {len(rec_power)} PowerHistory packets, {sum(len(e) for _, e in rec_power)} entries")
    log_power = [(p.ms, p.body["List"]) for p in game.packets if p.type == powerlog.POWER_HISTORY]

    # In step until the log's first RESET_GAME, against the stream that client received: the recording's packets
    # (a log of the game played) or the server's plan, whose fights are cut into one packet per block (a log of
    # watching the recording). Whichever matches longer is reported.
    first_reset = next((i for i, (_, es) in enumerate(log_power) if any("ResetGame" in e for e in es)), len(log_power))
    best = None
    for label, stream in (("the recording", rec_power), ("the server's plan", rec_plan)):
        n = min(first_reset, len(stream))
        diffs = [(i, diff(norm_packet(log_power[i][1]), norm_packet(stream[i][1]))) for i in range(n)]
        same = sum(1 for _, d in diffs if d is None)
        if best is None or same > best[1]:
            best = (label, same, n, [(i, d) for i, d in diffs if d])
    label, same, n, bad = best
    for i, d in bad[:args.show]:
        print(f"  packet {i}: {d}")
    print(f"in step with {label}: {same}/{n} packets identical before the log's first RESET_GAME (at log packet {first_reset})")

    # After it: content lookup.
    index: dict[str, int] = {}
    rec_plan_norm = [norm_packet(es) for _, es in rec_plan]
    first_of: dict[str, list[int]] = {}
    for i, es in enumerate(rec_plan_norm):
        index.setdefault(json.dumps(es, sort_keys=True), i)
        if es:
            first_of.setdefault(json.dumps(es[0], sort_keys=True), []).append(i)
    found = missing = resets = joined = 0
    kinds: Counter = Counter()
    for i in range(first_reset, len(log_power)):
        es = log_power[i][1]
        if any("ResetGame" in e for e in es):
            resets += 1
            continue
        if json.dumps(norm_packet(es), sort_keys=True) in index:
            found += 1
        elif (run := joined_run(norm_packet(es), rec_plan_norm, first_of)) is not None:
            joined += 1  # a jump into a fight sends the rest of it as one packet: several plan packets in a row
            if joined <= 3:
                print(f"  log packet {i} = plan packets {run[0]}..{run[1]} joined")
        else:
            missing += 1
            kinds[next(iter(es[0]))] += 1
            if missing <= args.show:
                first = es[0]
                desc = f"{next(iter(first))} {first.get('PowerStart', first.get('TagChange', {}))}"
                print(f"  log packet {i} ({len(es)} entries, first {desc}) has no identical recording packet")
    print(f"after it: {found} log packets found identical in the recording's plan ({len(rec_plan)} packets after the "
          f"fight splits), {joined} equal to a run of plan packets, {missing} not found ({dict(kinds)}), {resets} snapshots skipped")

    # Choices and options by id.
    def by_id(packets, ptype, msg, key=lambda b: b.get("Id")):
        return {key(b): b for b in (decode(msg, p.body) if hasattr(p, "body") and isinstance(p.body, bytes) else p.body
                                   for p in packets if p.type == ptype)}
    rec_s2c = [p for p in rec.packets if p.dir == "S2C"]
    for name, ptype, msg, norm in (("EntityChoices", powerlog.ENTITY_CHOICES, "PegasusGame.EntityChoices", lambda b: b),
                                   ("AllOptions", powerlog.ALL_OPTIONS, "PegasusGame.AllOptions", norm_options)):
        a = by_id(game.packets, ptype, msg)
        b = by_id(rec_s2c, ptype, msg)
        common = sorted(set(a) & set(b))
        bad = [(k, diff(norm(a[k]), norm(b[k]))) for k in common]
        bad = [(k, d) for k, d in bad if d]
        print(f"{name}: {len(a)} in log, {len(b)} in recording, {len(common)} ids shared, {len(common) - len(bad)} identical")
        for k, d in bad[:args.show]:
            print(f"  id {k}: {d}")
    # Inferred clicks against the recorded ones (same option id). Twin options (one card, same targets; they
    # differ only by PowerKeyword, which the log doesn't print) name the same action: counted as equivalent.
    rec_opts = by_id(rec_s2c, powerlog.ALL_OPTIONS, "PegasusGame.AllOptions")
    recorded = {c["Id"]: c for c in (decode("PegasusGame.ChooseOption", p.body) for p in rec.packets
                                     if p.dir == "C2S" and p.type == powerlog.CHOOSE_OPTION)}
    verdicts: Counter = Counter()
    wrong = []
    for p in game.packets:
        if p.type != powerlog.CHOOSE_OPTION:
            continue
        c, r = p.body, recorded.get(p.body["Id"])
        if r is None:
            verdicts["no recorded click for that id"] += 1
            continue
        opts = rec_opts[c["Id"]]["Options"]
        main = lambda i: (opts[i].get("MainOption") or {}).get("Id")
        if r["Index"] == c["Index"] and r.get("Target", 0) == c.get("Target", 0):
            verdicts["exact"] += 1
        elif main(r["Index"]) == main(c["Index"]) and opts[r["Index"]]["Type_"] == opts[c["Index"]]["Type_"]                 and r.get("Target", 0) == c.get("Target", 0):
            verdicts["twin option (same card and target)"] += 1
        else:
            verdicts["wrong"] += 1
            wrong.append((c["Id"], c["Index"], r["Index"]))
    log_ids = {p.body["Id"] for p in game.packets if p.type == powerlog.ALL_OPTIONS}
    inferred_ids = {p.body["Id"] for p in game.packets if p.type == powerlog.CHOOSE_OPTION}
    missed = sum(1 for i in log_ids if i in recorded and i not in inferred_ids)
    print(f"ChooseOption: {sum(verdicts.values())} inferred: {dict(verdicts)}; {missed} recorded clicks not inferred")
    for oid, got, want in wrong[:args.show]:
        print(f"  options {oid}: inferred #{got}, recorded #{want}")

    # The player's picks (SendChoices) against the recorded ones.
    rec_picks = {b["Id"]: b for b in (decode("PegasusGame.ChooseEntities", p.body) for p in rec.packets
                                      if p.dir == "C2S" and p.type == powerlog.CHOOSE_ENTITIES)}
    log_picks = {p.body["Id"]: p.body for p in game.packets if p.type == powerlog.CHOOSE_ENTITIES}
    same = sum(1 for k, b in log_picks.items() if k in rec_picks and b["Entities"] == rec_picks[k]["Entities"])
    print(f"ChooseEntities: {len(log_picks)} in log, {len(rec_picks)} in recording, {same} identical")

    # Rebuilt turn timers against the recorded ones (a log of watching a replay at speed has no real timing).
    t0_rec = next(p.ms for p in rec.packets if p.dir == "S2C" and p.type == powerlog.POWER_HISTORY)  # CreateGame
    t0_log = next(p.ms for p in game.packets if p.type == powerlog.POWER_HISTORY)
    def turn_timers(timers):
        """{turn: (first countdown's (ms, seconds), the Seconds 0 timer's ms)}"""
        out: dict[int, list] = {}
        for ms, b in timers:
            row = out.setdefault(b["Turn"], [None, None])
            if b["Seconds"] and row[0] is None:
                row[0] = (ms, b["Seconds"])
            elif not b["Seconds"]:
                row[1] = ms
        return out
    rec_timers = turn_timers((p.ms - t0_rec, decode("PegasusGame.TurnTimer", p.body)) for p in rec.packets
                             if p.dir == "S2C" and p.type == powerlog.TURN_TIMER)
    log_timers = turn_timers((p.ms - t0_log, p.body) for p in game.packets if p.type == powerlog.TURN_TIMER)
    starts, ends, zeros = Counter(), Counter(), Counter()
    for turn in sorted(set(rec_timers) | set(log_timers)):
        r, g = rec_timers.get(turn, [None, None]), log_timers.get(turn, [None, None])
        if r[0] and g[0]:
            # When the countdown appears and when it reaches zero, log against recording (s): what the player sees.
            starts[round((g[0][0] - r[0][0]) / 1000)] += 1
            ends[round((g[0][0] / 1000 + g[0][1]) - (r[0][0] / 1000 + r[0][1]))] += 1
        else:
            ends["only in " + ("recording" if r[0] else "log")] += 1
        if r[1] is not None and g[1] is not None:
            zeros[round((g[1] - r[1]) / 1000, 1)] += 1
        elif r[1] is not None or g[1] is not None:
            zeros["only in " + ("recording" if r[1] is not None else "log")] += 1
    fmt = lambda c: dict(sorted(c.items(), key=str))
    print(f"TurnTimer: {len(log_timers)} turns rebuilt, {len(rec_timers)} recorded; log minus recording (s): "
          f"countdown shown {fmt(starts)}, runs out {fmt(ends)}, Seconds 0 sent {fmt(zeros)}")

    # Through the server's loader, and the block notes against the combats' blocks.
    out = args.out or Path(tempfile.mkdtemp(prefix="powerlog_check_"))
    path = powerlog.write_game(game, out, parser.enums.build, force=True)
    data = ReplayData(load(path))
    notes = data._block_notes()
    print(f"server load: {len(data.plan)} plan packets, {len(data.points)} points, {len(data.blocks)} blocks, "
          f"{len(data.combats)} combats, {len(notes)} block notes -> {path}")
    for combat, point_idx in sorted(data.combat_block_points.items(), key=lambda kv: (kv[0] is None, kv[0] or 0)):
        mine = [data.points[i].block for i in point_idx]
        times = data._match_block_notes(mine, notes)
        label = "last (open)" if combat is None else str(combat)
        if times:
            print(f"  combat {label}: {len(mine)} blocks all timed by notes, {times[0] / 1000:.1f}-{times[-1] / 1000:.1f} s")
        else:
            print(f"  combat {label}: {len(mine)} blocks, notes don't cover them (ticks spread by estimate)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
