using System;
using System.Collections;
using System.Reflection;
using Blizzard.GameService.SDK.Client.Integration;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Watching a replay: the Replays page's Watch starts a Battlegrounds find-game with a one-shot watch request,
    /// and that find-game joins the replay server on localhost instead of queueing with Battle.net (every other
    /// find-game, the lobby's Play included, is left alone). GameMgr.FindGameInternal has already set the next
    /// game type/mission and moved
    /// to CLIENT_STARTED when it calls Network.FindGame; we skip that Battle.net request and, one frame later
    /// (after FindGame shows its transition popup), fire SERVER_GAME_CONNECTING — the same sequence
    /// GameMgr.SpectateGame uses, and like it as a spectator (see <see cref="SpectatorJoin"/>).
    /// </summary>
    internal static class LocalServer
    {
        private static readonly MethodInfo ChangeFindGameState = AccessTools.Method(
            typeof(GameMgr), "ChangeFindGameState", new[] { typeof(FindGameState), typeof(GameServerInfo) });

        /// <summary>A replay started from the Replays page is (or may be) on.</summary>
        public static bool Active => _inWatch;

        // Watch (Replays page): a one-shot request the next find-game consumes, then "in a replay"
        // until the client is back out of it. A real find-game always clears it, so a failed watch can never
        // turn the lobby's Play into a local join.
        private static bool _watchPending, _inWatch;

        /// <summary>Leaving the game should land in the Battlegrounds lobby: during a replay and after it, until the
        /// next game. The client asks for the post-game scene more than once on the way out (the menu's Leave, then
        /// SpectatorManager.EndSpectatorMode after the disconnect); a HUB answer to a later ask starts a second
        /// scene switch while the first has the game scene unloading, and neither ever finishes.</summary>
        public static bool ReturnsToBattlegrounds => _inWatch || _afterWatch;
        private static bool _afterWatch;

        public static void RequestWatch() => _watchPending = true;

        /// <summary>From the Watch until the client is back out of the replay (see <see cref="ReplayTelemetry"/>).</summary>
        public static bool WatchRequestedOrOn => _watchPending || _inWatch;

        /// <summary>When the last watch ended (unscaled time).</summary>
        public static float WatchOverAt { get; private set; } = -1000f;

        /// <summary>For InterceptFindGame: whether this find-game joins locally.</summary>
        internal static bool TakeJoinRequest()
        {
            bool watch = _watchPending;
            _watchPending = false;
            _inWatch = watch;
            _afterWatch = false;
            _localJoin = watch;
            return _localJoin;
        }

        // A local join that is cancelled (the transition popup's Cancel) while already connected: the client
        // drops the find-game but keeps the game-server socket, and the next join then fails ("GotoGameServer
        // called when we're already waiting for a game to start"). Close that socket ourselves.
        private static bool _localJoin, _findGameHooked;

        private static bool OnFindGameEvent(FindGameEventData eventData, object userData)
        {
            if (!_localJoin) return false;
            switch (eventData.m_state)
            {
                case FindGameState.SERVER_GAME_STARTED:
                    _localJoin = false;
                    break;
                case FindGameState.CLIENT_CANCELED:
                case FindGameState.CLIENT_ERROR:
                case FindGameState.SERVER_GAME_CANCELED:
                    _localJoin = false;
                    var network = global::Network.Get();
                    if (network != null && network.IsConnectedToGameServer())
                    {
                        Plugin.Log.LogInfo($"Local join {eventData.m_state}: closing the game-server connection");
                        network.DisconnectFromGameServer(global::Network.DisconnectReason.Unload);
                    }
                    break;
            }
            return false;
        }

        /// <summary>Per frame: a watch ends once the client is out of the game and not looking for one.</summary>
        public static void Update()
        {
            if (!_findGameHooked && GameMgr.Get() != null)
            {
                GameMgr.Get().RegisterFindGameEvent(OnFindGameEvent);
                _findGameHooked = true;
            }
            var mode = SceneMgr.Get()?.GetMode();
            if (_afterWatch && mode == SceneMgr.Mode.GAMEPLAY && !_inWatch) _afterWatch = false; // another game (a friend's)
            if (!_inWatch) return;
            var network = global::Network.Get();
            if (mode != SceneMgr.Mode.GAMEPLAY && !GameMgr.Get().IsFindingGame() &&
                (network == null || !network.IsConnectedToGameServer()))
            {
                _inWatch = false;
                _afterWatch = true;
                WatchOverAt = Time.unscaledTime;
                Plugin.Log.LogInfo("Replay watch over");
                ReplayServerProcess.Stop("the replay is over");
            }
        }

        public static IEnumerator ConnectNextFrame()
        {
            yield return null;
            var info = new GameServerInfo
            {
                Address = Plugin.LocalServerHost.Value,
                Port = (uint)Plugin.LocalServerPort.Value,
                GameHandle = (uint)Plugin.LocalGameHandle.Value,
                ClientHandle = 1,
                AuroraPassword = "local",
                Version = "local",
                Mission = GameMgr.Get().GetNextMissionId(),
                SpectatorMode = true,
                SpectatorPassword = "replay",
            };
            SpectatorJoin.PrepareJoin();
            Plugin.Log.LogInfo($"Local mode: connecting to {info.Address}:{info.Port} (mission {info.Mission})");
            if (ChangeFindGameState == null)
            {
                Plugin.Log.LogError("GameMgr.ChangeFindGameState(FindGameState, GameServerInfo) not found");
                yield break;
            }
            ChangeFindGameState.Invoke(GameMgr.Get(), new object[] { FindGameState.SERVER_GAME_CONNECTING, info });
        }
    }

    [HarmonyPatch(typeof(global::Network), nameof(global::Network.FindGame))]
    internal static class InterceptFindGame
    {
        private static bool Prefix(PegasusShared.GameType gameType, int scenarioId)
        {
            if (!LocalServer.TakeJoinRequest()) return true;
            if (!gameType.ToString().StartsWith("GT_BATTLEGROUNDS"))
            {
                Plugin.Log.LogInfo($"Local mode: {gameType} is not Battlegrounds, queueing normally");
                return true;
            }
            Plugin.Log.LogInfo($"Local mode: skipping Battle.net queue for {gameType} scenario {scenarioId}");
            Plugin.Instance.StartCoroutine(LocalServer.ConnectNextFrame());
            return false;
        }
    }
}
