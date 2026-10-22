"""Generate hsserver/schema.json from the client's decompiled protobuf classes (SilentOrbit style).

The game's message classes live in blizzard.bgsclient.dll. Decompile it first (ilspycmd 9.x):

    ilspycmd -p -o <out>\\blizzard.bgsclient -r <HS>\\Hearthstone_Data\\Managed <HS>\\Hearthstone_Data\\Managed\\blizzard.bgsclient.dll
    python tools/gen_schema.py <out>\\blizzard.bgsclient

Every class's `Deserialize(Stream, T instance, long limit)` is read: each `case <key>:` (fields 1-15) and each
`case <n>u:` inside `switch (key.Field)` (fields 16+) maps a protobuf field to a property and a reader. Packing
is recorded per field, because the client only accepts each repeated field in the form it reads (e.g.
EntityChoices.Entities is packed, UnchoosableEntities is not).
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

WIRE = {"Varint": 0, "Fixed64": 1, "LengthDelimited": 2, "Fixed32": 5}

DESER_RE = re.compile(r"public static (\w+) Deserialize\(Stream stream, \1 instance, long limit\)\s*\{(.*?)\n\t\}\n", re.S)
NAMESPACE_RE = re.compile(r"^namespace ([\w.]+);", re.M)
NESTED_CLASS_RE = re.compile(r"^\t(?:public|internal) class (\w+)", re.M)
CASE_KEY_RE = re.compile(r"\n\t\t\tcase (\d+):\n(.*?)(?=\n\t\t\tcase |\n\t\t\tdefault:)", re.S)
CASE_FIELD_RE = re.compile(r"case (\d+)u:\s*if \(key\.WireType == Wire\.(\w+)\)\s*\{(.*?)\bbreak;", re.S)
# The statement that stores the value: "instance.X = ..." or "instance.X.Add(...)". Single nested messages are
# read as "if (instance.X == null) { instance.X = T.DeserializeLengthDelimited(...) } else {...}".
STORE_RE = re.compile(r"instance\.\w+(\.Add\(|\s*=\s*(?!=))")


def store_line(block: str) -> str | None:
    return next((ln for ln in block.splitlines() if STORE_RE.search(ln)), None)


def classify(stmt: str, cls: str) -> tuple[str, str, bool] | None:
    """(property, type, repeated) from one reader statement, or None."""
    m = re.search(r"instance\.(\w+)(\.Add\(|\s*=\s*(?!=))(.*)", stmt, re.S)
    if not m:
        return None
    prop, op, expr = m.group(1), m.group(2), m.group(3)
    repeated = op.startswith(".Add")
    if "DeserializeLengthDelimited" in expr:
        ref = re.search(r"([\w.]+)\.DeserializeLengthDelimited", expr)
        typ = "msg:" + (ref.group(1) if ref else cls)  # no prefix = the class reading itself
    elif "ReadBool" in expr:
        typ = "bool"
    elif "ReadString" in expr:
        typ = "string"
    elif "ReadBytes" in expr:
        typ = "bytes"
    elif "ReadSingle" in expr:
        typ = "float"
    elif "ReadDouble" in expr:
        typ = "double"
    elif "ReadUInt64" in expr or "ReadUInt32(stream)" in expr and "ProtocolParser" in expr:
        cast = re.match(r"\s*\(([\w.]+)\)", expr)  # enum casts look like (PowerHistoryStart.Type)
        typ = "uint64" if cast is None or cast.group(1) in ("ulong", "uint") else "int64"
    elif re.search(r"\.ReadUInt32\(\)", expr):
        typ = "fixed32"
    elif re.search(r"\.ReadUInt64\(\)", expr):
        typ = "fixed64"
    else:
        return None
    return prop, typ, repeated


def parse_body(body: str, cls: str) -> dict[int, dict]:
    fields: dict[int, dict] = {}
    for m in CASE_KEY_RE.finditer(body):
        key, block = int(m.group(1)), m.group(2)
        if key < 0:
            continue
        field_no, wire = key >> 3, key & 7
        packed = "num2 += stream.Position" in block
        stmt = store_line(block)
        info = classify(stmt, cls) if stmt else None
        if info:
            prop, typ, repeated = info
            fields[field_no] = {"name": prop, "type": typ, "repeated": repeated or packed, "packed": packed, "wire": wire}
    for m in CASE_FIELD_RE.finditer(body):
        field_no, wire_name, block = int(m.group(1)), m.group(2), m.group(3)
        packed = "num2 += stream.Position" in block
        stmt = store_line(block)
        info = classify(stmt, cls) if stmt else None
        if info:
            prop, typ, repeated = info
            fields[field_no] = {"name": prop, "type": typ, "repeated": repeated or packed, "packed": packed,
                                "wire": WIRE.get(wire_name, -1)}
    return fields


def main() -> None:
    root = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parents[1] / "hsserver" / "schema.json"
    schema: dict[str, dict] = {}
    for path in sorted(root.rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if "Deserialize(Stream stream," not in text:
            continue
        ns_m = NAMESPACE_RE.search(text)
        ns = ns_m.group(1) if ns_m else ""
        for m in DESER_RE.finditer(text):
            cls, body = m.group(1), m.group(2)
            fields = parse_body(body, cls)
            # Empty messages (e.g. PowerHistoryEnd) are kept: they are real, field-less payloads.
            schema[f"{ns}.{cls}" if ns else cls] = {str(k): v for k, v in sorted(fields.items())}
    # Resolve "msg:Name" to a fully qualified name: same namespace first, then any unique match.
    by_short: dict[str, list[str]] = {}
    for full in schema:
        by_short.setdefault(full.rsplit(".", 1)[-1], []).append(full)
    unresolved = set()
    for full, fields in schema.items():
        ns = full.rsplit(".", 1)[0] if "." in full else ""
        for f in fields.values():
            if not f["type"].startswith("msg:"):
                continue
            ref = f["type"][4:]
            if ref in schema:
                continue
            short = ref.rsplit(".", 1)[-1]
            cands = by_short.get(short, [])
            same_ns = [c for c in cands if c.rsplit(".", 1)[0] == ns]
            pick = same_ns[0] if same_ns else (cands[0] if len(cands) == 1 else None)
            if pick:
                f["type"] = "msg:" + pick
            else:
                unresolved.add(ref)
    out.write_text(json.dumps(schema, indent=1, sort_keys=True), encoding="utf-8")
    game = sum(1 for k in schema if k.startswith("PegasusGame."))
    print(f"{len(schema)} messages ({game} PegasusGame) -> {out}")
    if unresolved:
        print("unresolved message refs:", ", ".join(sorted(unresolved)))


if __name__ == "__main__":
    main()
