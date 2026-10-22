using Blizzard.GameService.SDK.Client.Integration;
using HarmonyLib;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replays are watched as a spectator: the client itself then blocks every action (hover and tooltips
    /// still work), applies the replayed hero pick (MulliganManager's spectator branch) and sends no moves.
    ///
    /// <see cref="LocalServer"/> joins with GameServerInfo.SpectatorMode and GameMgr.m_nextSpectator, so the
    /// handshake is a SpectatorHandshake and GameMgr.IsSpectator() turns on at GameSetup. A spectator has no
    /// local player: GameState.GetLocalSidePlayer takes the player whose GameAccountId equals
    /// SpectatorManager.m_spectateeFriendlySide (normally set by the Battle.net friend flow). A Battlegrounds
    /// CreateGame holds exactly two players, the watched one with a real account id and Bob's side with 0/0,
    /// so the watched player is picked from each CreateGame (game start and every jump's reset) before the
    /// client assigns sides. The value is cleared after the game: left set, it would stop friends from
    /// spectating real games (SpectatorManager.CanAddSpectators).
    /// </summary>
    internal static class SpectatorJoin
    {
        private static readonly AccessTools.FieldRef<SpectatorManager, BnetGameAccountId> Spectatee =
            AccessTools.FieldRefAccess<SpectatorManager, BnetGameAccountId>("m_spectateeFriendlySide");

        private static readonly AccessTools.FieldRef<GameMgr, bool> NextSpectator =
            AccessTools.FieldRefAccess<GameMgr, bool>("m_nextSpectator");

        private static BnetGameAccountId _ours;
        private static bool _joining;

        /// <summary>Called right before the local join: make the coming game a spectated one.</summary>
        public static void PrepareJoin()
        {
            NextSpectator(GameMgr.Get()) = true;
            _joining = true;
        }

        /// <summary>Per frame: forget the watched player once the local game is over.</summary>
        public static void Update()
        {
            if (ReplaySpeed.InLocalGame)
            {
                _joining = false;
                return;
            }
            if (_joining || _ours == null) return;
            var manager = SpectatorManager.Get();
            if (manager != null && ReferenceEquals(Spectatee(manager), _ours)) Spectatee(manager) = null;
            _ours = null;
        }

        private static void PickWatchedPlayer(Network.HistCreateGame createGame)
        {
            if (createGame?.Players == null || !ReplaySpeed.InLocalGame || !GameMgr.Get().IsSpectator()) return;
            BnetGameAccountId watched = null;
            foreach (var player in createGame.Players)
            {
                var id = player.GameAccountId;
                if (id == null || (id.High == 0 && id.Low == 0)) continue;
                if (watched != null)
                {
                    Plugin.Log.LogWarning("Spectator join: more than one player with an account id; watching the first");
                    break;
                }
                watched = id;
            }
            if (watched == null)
            {
                Plugin.Log.LogWarning("Spectator join: no player with an account id in CreateGame; every side will be opposing");
                return;
            }
            var manager = SpectatorManager.Get();
            if (manager == null) return;
            _ours = new BnetGameAccountId(watched.High, watched.Low);
            Spectatee(manager) = _ours;
        }

        [HarmonyPatch(typeof(GameState), nameof(GameState.OnRealTimeCreateGame))]
        private static class OnCreateGame
        {
            private static void Prefix(Network.HistCreateGame createGame) => PickWatchedPlayer(createGame);
        }

        [HarmonyPatch(typeof(GameState), nameof(GameState.OnResetGame))]
        private static class OnResetGame
        {
            private static void Prefix(Network.HistResetGame resetGame) => PickWatchedPlayer(resetGame?.CreateGame);
        }

        /// <summary>The escape menu's Leave only acts inside a Battle.net spectator party; leave a local replay too.</summary>
        [HarmonyPatch(typeof(GameMenu), "LeaveButtonPressed")]
        private static class LeaveFromMenu
        {
            private static void Prefix()
            {
                if (ReplaySpeed.InLocalGame && GameMgr.Get().IsSpectator()) SpectatorManager.Get().LeaveSpectatorMode();
            }
        }

        /// <summary>After a watched replay, go back to the Battlegrounds lobby (a spectator normally lands in the hub).</summary>
        [HarmonyPatch(typeof(GameMgr), "GetSpectatorPostGameSceneMode")]
        private static class BackToBattlegrounds
        {
            private static void Postfix(ref SceneMgr.Mode __result)
            {
                if (LocalServer.ReturnsToBattlegrounds) __result = SceneMgr.Mode.BACON;
            }
        }
    }
}
