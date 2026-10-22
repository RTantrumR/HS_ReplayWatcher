"""Typed protobuf codec for the game's messages, driven by hsserver/schema.json (see tools/gen_schema.py).

decode(msg, data) -> dict keyed by the client's property names (repeated -> list, nested -> dict).
encode(msg, dict) -> bytes, fields in ascending field order, packed exactly where the client reads packed.
Fields the schema doesn't know are kept under "_unknown" as (field_no, wire, raw) and written back, so
decode/encode round-trips byte-exact (checked against real recordings by tools/check_proto_roundtrip.py).
"""
from __future__ import annotations

import json
import struct
from functools import lru_cache
from pathlib import Path

from .packets import encode_varint

SCHEMA_PATH = Path(__file__).with_name("schema.json")


@lru_cache(maxsize=1)
def schema() -> dict[str, dict[int, dict]]:
    raw = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    return {msg: {int(no): f for no, f in fields.items()} for msg, fields in raw.items()}


def _read_varint(buf: bytes, pos: int) -> tuple[int, int]:
    result = shift = 0
    while True:
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, pos
        shift += 7


def _signed64(v: int) -> int:
    return v - (1 << 64) if v >= 1 << 63 else v


def _read_scalar(typ: str, buf: bytes, pos: int):
    if typ in ("int64", "uint64", "bool"):
        v, pos = _read_varint(buf, pos)
        if typ == "int64":
            v = _signed64(v)
        elif typ == "bool":
            v = bool(v)
        return v, pos
    if typ == "float":
        return struct.unpack_from("<f", buf, pos)[0], pos + 4
    if typ == "fixed32":
        return struct.unpack_from("<I", buf, pos)[0], pos + 4
    if typ == "double":
        return struct.unpack_from("<d", buf, pos)[0], pos + 8
    if typ == "fixed64":
        return struct.unpack_from("<Q", buf, pos)[0], pos + 8
    raise ValueError(f"not a scalar: {typ}")


def _skip(buf: bytes, pos: int, wire: int) -> int:
    if wire == 0:
        return _read_varint(buf, pos)[1]
    if wire == 1:
        return pos + 8
    if wire == 2:
        ln, pos = _read_varint(buf, pos)
        return pos + ln
    if wire == 5:
        return pos + 4
    raise ValueError(f"unsupported wire type {wire}")


def decode(msg: str, buf: bytes) -> dict:
    fields = schema()[msg]
    out: dict = {}
    pos = 0
    while pos < len(buf):
        key, pos = _read_varint(buf, pos)
        no, wire = key >> 3, key & 7
        f = fields.get(no)
        if f is None or (wire == 2) != (f["type"] in ("string", "bytes") or f["type"].startswith("msg:") or f["packed"]):
            start = pos
            pos = _skip(buf, pos, wire)
            out.setdefault("_unknown", []).append((no, wire, buf[start:pos]))
            continue
        name, typ = f["name"], f["type"]
        if f["packed"]:
            ln, pos = _read_varint(buf, pos)
            end = pos + ln
            vals = out.setdefault(name, [])
            while pos < end:
                v, pos = _read_scalar(typ, buf, pos)
                vals.append(v)
            continue
        if typ.startswith("msg:"):
            ln, pos = _read_varint(buf, pos)
            v = decode(typ[4:], buf[pos:pos + ln])
            pos += ln
        elif typ in ("string", "bytes"):
            ln, pos = _read_varint(buf, pos)
            raw = buf[pos:pos + ln]
            pos += ln
            v = raw.decode("utf-8") if typ == "string" else raw
        else:
            v, pos = _read_scalar(typ, buf, pos)
        if f["repeated"]:
            out.setdefault(name, []).append(v)
        else:
            out[name] = v
    return out


def _encode_scalar(typ: str, v) -> bytes:
    if typ in ("int64", "uint64"):
        return encode_varint(int(v))
    if typ == "bool":
        return b"\x01" if v else b"\x00"
    if typ == "float":
        return struct.pack("<f", v)
    if typ == "fixed32":
        return struct.pack("<I", v)
    if typ == "double":
        return struct.pack("<d", v)
    if typ == "fixed64":
        return struct.pack("<Q", v)
    raise ValueError(f"not a scalar: {typ}")


def _wire_of(f: dict) -> int:
    t = f["type"]
    if f["packed"] or t in ("string", "bytes") or t.startswith("msg:"):
        return 2
    if t in ("float", "fixed32"):
        return 5
    if t in ("double", "fixed64"):
        return 1
    return 0


def _encode_one(f: dict, no: int, v) -> bytes:
    t = f["type"]
    key = encode_varint(no << 3 | _wire_of(f))
    if t.startswith("msg:"):
        body = encode(t[4:], v)
        return key + encode_varint(len(body)) + body
    if t in ("string", "bytes"):
        raw = v.encode("utf-8") if isinstance(v, str) else bytes(v)
        return key + encode_varint(len(raw)) + raw
    return key + _encode_scalar(t, v)


def encode(msg: str, obj: dict) -> bytes:
    fields = schema()[msg]
    parts: list[tuple[int, bytes]] = []
    for no, f in fields.items():
        name = f["name"]
        if name not in obj or obj[name] is None:
            continue
        v = obj[name]
        if f["packed"]:
            if not v:
                continue
            body = b"".join(_encode_scalar(f["type"], x) for x in v)
            parts.append((no, encode_varint(no << 3 | 2) + encode_varint(len(body)) + body))
        elif f["repeated"]:
            parts.append((no, b"".join(_encode_one(f, no, x) for x in v)))
        else:
            parts.append((no, _encode_one(f, no, v)))
    for no, wire, raw in obj.get("_unknown", []):
        # raw is everything after the key as it appeared on the wire (incl. a length prefix for wire type 2)
        parts.append((no, encode_varint(no << 3 | wire) + raw))
    parts.sort(key=lambda p: p[0])
    return b"".join(p[1] for p in parts)
