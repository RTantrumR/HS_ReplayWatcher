using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// A replay never happened, so the client sends Blizzard nothing about it. Its telemetry would otherwise report
    /// a game on 127.0.0.1 (the connection-quality samples carry the game server's address and port), the
    /// spectator join, and the "live issue" reports our rewinds can set off in GameState.
    ///
    /// Every telemetry message goes through TelemetryClient's EnqueueMessage overloads (the only user of the
    /// telemetry SDK); they drop messages from the Watch until 5 s after the client is back out of the replay.
    /// The network component's quality sampler is started at the connect and flushes on the disconnect, so it is
    /// never started for a loopback game server either. Presence (friends see "spectating Battlegrounds") is a
    /// separate Battle.net channel and is left alone.
    /// </summary>
    internal static class ReplayTelemetry
    {
        private const float TailSeconds = 5f;

        private static readonly Dictionary<string, int> Dropped = new Dictionary<string, int>();

        public static bool Muted =>
            LocalServer.WatchRequestedOrOn || Time.unscaledTime - LocalServer.WatchOverAt < TailSeconds;

        internal static void Drop(string name)
        {
            Dropped.TryGetValue(name, out int n);
            Dropped[name] = n + 1;
        }

        /// <summary>Per frame: once the window has closed, log what was held back.</summary>
        public static void Update()
        {
            if (Dropped.Count == 0 || Muted) return;
            Plugin.Log.LogInfo($"Replay telemetry: {Dropped.Values.Sum()} message(s) not sent (" +
                               string.Join(", ", Dropped.Select(kv => $"{kv.Key} x{kv.Value}")) + ")");
            Dropped.Clear();
        }
    }

    [HarmonyPatch]
    internal static class MuteReplayTelemetry
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(HearthstoneTelemetry.TelemetryClient))
                       .Where(m => m.Name == "EnqueueMessage" && m.ReturnType == typeof(long));

        private static bool Prefix(object[] __args, ref long __result)
        {
            if (!ReplayTelemetry.Muted) return true;
            object first = __args.Length > 0 ? __args[0] : null;
            string name = first is string package && __args.Length > 1 ? $"{package}.{__args[1]}"
                        : first is Type type ? type.Name
                        : first?.GetType().Name ?? "?";
            // A quality sample of another connection (Battle.net's own) is true and goes out; a loopback one never
            // gets a sampler (below).
            if (name == "TcpQualitySample")
            {
                string address = Traverse.Create(first).Property("Address4").GetValue()?.ToString() ?? "";
                if (!address.StartsWith("127.")) return true;
                name += " " + address;
            }
            ReplayTelemetry.Drop(name);
            __result = 0L;
            return false;
        }
    }

    [HarmonyPatch(typeof(TelemetryManagerComponentNetwork), nameof(TelemetryManagerComponentNetwork.OnSendNetworkTelemetryEvents))]
    internal static class NoLoopbackNetworkTelemetry
    {
        private static bool Prefix(TelemetryManagerComponentNetwork.NetworkTelemetryEvent telemetryEvent, IPEndPoint endPoint)
        {
            if (endPoint == null || !IPAddress.IsLoopback(endPoint.Address)) return true;
            if (telemetryEvent == TelemetryManagerComponentNetwork.NetworkTelemetryEvent.CONNECT)
                Plugin.Log.LogInfo($"Replay telemetry: no quality sampler for {endPoint}");
            return false;
        }
    }
}
