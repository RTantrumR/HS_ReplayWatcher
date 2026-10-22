using System;
using Blizzard.GameService.SDK.Client.Integration;
using Blizzard.T5.Network;
using HarmonyLib;
using Networking;

namespace HsPuzzleBridge
{
    // Incoming game-server packets: raw type + body, before decoding. Runs on the socket thread.
    // Only the game RpcController feeds this method (QueueDispatcher ctor), so util/BGS traffic never shows up.
    [HarmonyPatch(typeof(QueueDispatcher), "OnPacketReceived", new[] { typeof(IPacket), typeof(Action) })]
    internal static class RecordIncoming
    {
        private static void Prefix(IPacket packet)
        {
            try
            {
                if (packet == null) return;
                byte[] body = packet.body;
                PacketRecorder.Packet("S2C", packet.packetType, body, 0, body?.Length ?? 0);
            }
            catch (Exception e) { Plugin.Log.LogError("RecordIncoming: " + e); }
        }
    }

    // Outgoing game-server packets. EncodePacket's only caller is SendGamePacket; the result is
    // [int32 type][int32 size][body], so the body starts at offset 8.
    [HarmonyPatch(typeof(QueueDispatcher), nameof(QueueDispatcher.EncodePacket))]
    internal static class RecordOutgoing
    {
        private static void Postfix(int packetType, byte[] __result)
        {
            try
            {
                if (__result == null || __result.Length < 8) return;
                PacketRecorder.Packet("C2S", packetType, __result, 8, __result.Length - 8);
            }
            catch (Exception e) { Plugin.Log.LogError("RecordOutgoing: " + e); }
        }
    }

    // Start a new recording per game connection, with everything we know about how the client got here.
    [HarmonyPatch(typeof(global::Network), nameof(global::Network.GotoGameServer))]
    internal static class RecordConnect
    {
        private static void Prefix(GameServerInfo info, bool reconnecting)
        {
            try
            {
                if (info == null) return;
                var mgr = GameMgr.Get();
                string meta =
                    $"\"address\":{Json.Str(info.Address)},\"port\":{info.Port}," +
                    $"\"gameHandle\":{info.GameHandle},\"clientHandle\":{info.ClientHandle}," +
                    $"\"auroraPassword\":{Json.Str(info.AuroraPassword)},\"version\":{Json.Str(info.Version)}," +
                    $"\"mission\":{info.Mission},\"brawlLibraryItemId\":{info.BrawlLibraryItemId}," +
                    $"\"spectatorPassword\":{Json.Str(info.SpectatorPassword)},\"spectatorMode\":{(info.SpectatorMode ? "true" : "false")}," +
                    $"\"reconnecting\":{(reconnecting ? "true" : "false")}," +
                    $"\"nextGameType\":{Json.Str(mgr?.GetNextGameType().ToString())}," +
                    $"\"nextFormatType\":{Json.Str(mgr?.GetNextFormatType().ToString())}," +
                    $"\"nextMissionId\":{(mgr != null ? mgr.GetNextMissionId() : 0)}," +
                    $"\"nextSpectator\":{(mgr != null && mgr.IsNextSpectator() ? "true" : "false")}," +
                    $"\"nextReconnect\":{(mgr != null && mgr.IsNextReconnect() ? "true" : "false")}," +
                    $"\"clientVersion\":{Json.Str(global::Network.ProductVersion())}";
                // The local replay server's games (LocalServer's join) are replays being watched.
                bool local = info.AuroraPassword == "local" || info.Address == Plugin.LocalServerHost.Value;
                // Every spectator join (GameMgr.SpectateGame, Network.SpectateSecondPlayer) sets SpectatorMode.
                PacketRecorder.StartSession(meta, info.GameHandle, local, info.SpectatorMode);
            }
            catch (Exception e) { Plugin.Log.LogError("RecordConnect: " + e); }
        }
    }
}
