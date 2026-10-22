"""Game-server packet framing, IDs, and a schema-less protobuf wire decoder.

Framing (Blizzard.T5.Network.PacketEncoder, QueueDispatcher.EncodePacket in client 36.6):
    int32 LE packet type | int32 LE body size | protobuf body
"""
from __future__ import annotations

import struct
from dataclasses import dataclass

HEADER = struct.Struct("<ii")

# PacketID enums of blizzard.bgsclient PegasusGame.* (client 36.6), plus the two Deadend decoders
# registered in Networking.PacketDecoderManager.
PACKET_NAMES: dict[int, str] = {
    1: "GetGameState", 2: "ChooseOption", 3: "ChooseEntities", 5: "DebugMessage",
    6: "AIDebugInformation", 7: "ScriptDebugInformation", 8: "RopeTimerDebugInformation",
    9: "TurnTimer", 10: "NAckOption", 11: "Concede", 12: "GameCanceled", 13: "EntitiesChosen",
    14: "AllOptions", 15: "UserUI", 16: "GameSetup", 17: "EntityChoices", 19: "PowerHistory",
    22: "SpectatorHandshake", 23: "ServerResult", 24: "SpectatorNotify", 25: "InviteToSpectate",
    26: "RemoveSpectators", 30: "GameRoundHistory", 31: "GameRealTimeBattlefieldRaces",
    32: "GetGameRoundHistory", 33: "GetGameRealTimeBattlefieldRaces", 34: "BattlegroundsRatingChange",
    35: "GameGuardianVars", 36: "PlayerRealTimeBattlefieldRaces", 37: "TeammatesEntities",
    38: "TeammatesChooseEntities", 39: "TeammatesEntitiesChosen", 40: "PingTeammateEntity",
    41: "EntityPinged", 42: "RequestTeammatesGamestate", 43: "MulliganChooseOneTentativeSelect",
    44: "MulliganChooseOneTentativeSelection", 45: "NotifyTeammateChooseOne", 46: "ResendOptions",
    50: "ScriptLogMessage", 51: "GameLogRelayDeprecated", 52: "AchievementProgress",
    53: "UpdateBattlegroundInfo", 54: "GetBattlegroundHeroArmorTierList",
    55: "SetBattlegroundHeroBuddyProgress", 56: "SetBattlegroundHeroBuddyGained",
    57: "ReplaceBattlegroundHero", 58: "ClientScriptGameEvent", 59: "TeammateConcede",
    60: "ToggleAIPlayer", 61: "GetBattlegroundsPlayerAnomaly", 62: "FakeConcede",
    63: "PreRefreshBGHeroes", 64: "RequestReplaceBattlegroundsMulliganHero",
    65: "ReplaceBattlegroundMulliganHero", 66: "RequestReplaceAllBattlegroundsMulliganHeroExcept",
    67: "FeedPet", 68: "GeneratePetTreat", 69: "BattlegroundsMinionPool",
    70: "RequestBattlegroundsMinionPool", 71: "ToggleReplaceLeftPlayerByBot",
    72: "SetPetLevelOverride", 73: "ShopChoiceEnded", 74: "TeammateShopChoiceEnded",
    115: "Ping", 116: "Pong", 167: "DeadendUtil", 168: "Handshake", 169: "Deadend",
}


def packet_name(ptype: int) -> str:
    return PACKET_NAMES.get(ptype, f"Unknown{ptype}")


def frame(ptype: int, body: bytes) -> bytes:
    return HEADER.pack(ptype, len(body)) + body


@dataclass
class Packet:
    type: int
    body: bytes

    @property
    def name(self) -> str:
        return packet_name(self.type)


class FrameReader:
    """Incremental splitter for a TCP byte stream into packets."""

    def __init__(self) -> None:
        self._buf = bytearray()

    def feed(self, data: bytes) -> list[Packet]:
        self._buf += data
        out: list[Packet] = []
        while len(self._buf) >= HEADER.size:
            ptype, size = HEADER.unpack_from(self._buf)
            if len(self._buf) < HEADER.size + size:
                break
            body = bytes(self._buf[HEADER.size:HEADER.size + size])
            del self._buf[:HEADER.size + size]
            out.append(Packet(ptype, body))
        return out


# ---- minimal protobuf encoding (varint fields only) ---------------------------------------------

def encode_varint(n: int) -> bytes:
    if n < 0:
        n += 1 << 64  # int32/int64 negatives are sent as 64-bit two's complement
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def encode_varint_fields(fields: list[tuple[int, int]]) -> bytes:
    """Encode [(field_no, int_value), ...] as varint fields (wire type 0), in the given order."""
    return b"".join(encode_varint(no << 3) + encode_varint(val) for no, val in fields)


# ---- schema-less protobuf decoding -------------------------------------------------------------

def _varint(buf: bytes, pos: int) -> tuple[int, int]:
    result = shift = 0
    while True:
        if pos >= len(buf):
            raise ValueError("truncated varint")
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, pos
        shift += 7
        if shift > 70:
            raise ValueError("varint too long")


def decode_raw(buf: bytes) -> list[tuple[int, str, object]]:
    """Decode a protobuf message without a schema: [(field_no, wire_kind, value), ...].

    Length-delimited fields are tried as nested messages first, then UTF-8 text, then left as bytes.
    Raises ValueError when buf is not a well-formed message.
    """
    fields: list[tuple[int, str, object]] = []
    pos = 0
    while pos < len(buf):
        key, pos = _varint(buf, pos)
        field_no, wire = key >> 3, key & 7
        if field_no == 0:
            raise ValueError("field number 0")
        if wire == 0:
            val, pos = _varint(buf, pos)
            fields.append((field_no, "varint", val))
        elif wire == 1:
            if pos + 8 > len(buf):
                raise ValueError("truncated fixed64")
            fields.append((field_no, "fixed64", struct.unpack_from("<Q", buf, pos)[0]))
            pos += 8
        elif wire == 2:
            ln, pos = _varint(buf, pos)
            if pos + ln > len(buf):
                raise ValueError("truncated bytes")
            chunk = buf[pos:pos + ln]
            pos += ln
            fields.append((field_no, "bytes", _guess(chunk)))
        elif wire == 5:
            if pos + 4 > len(buf):
                raise ValueError("truncated fixed32")
            fields.append((field_no, "fixed32", struct.unpack_from("<I", buf, pos)[0]))
            pos += 4
        else:
            raise ValueError(f"unsupported wire type {wire}")
    return fields


def _guess(chunk: bytes) -> object:
    if chunk:
        try:
            return decode_raw(chunk)
        except ValueError:
            pass
    try:
        text = chunk.decode("utf-8")
        if text.isprintable():
            return text
    except UnicodeDecodeError:
        pass
    return chunk


def format_raw(fields: list[tuple[int, str, object]], indent: int = 0, max_lines: int | None = None) -> str:
    lines: list[str] = []

    def walk(fs: list[tuple[int, str, object]], depth: int) -> None:
        for no, kind, val in fs:
            if max_lines is not None and len(lines) >= max_lines:
                return
            pad = "  " * depth
            if isinstance(val, list):
                lines.append(f"{pad}{no} {{")
                walk(val, depth + 1)
                lines.append(f"{pad}}}")
            elif isinstance(val, bytes):
                lines.append(f"{pad}{no}: <{len(val)} bytes> {val[:24].hex()}")
            else:
                lines.append(f"{pad}{no}: {val!r}")

    walk(fields, indent)
    return "\n".join(lines)
