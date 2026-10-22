using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replay: refresh the BG UI a full reset doesn't touch.
    ///
    /// The rewind rebuilds every entity, but some BG UI only updates on events a rebuild never fires:
    /// - PlayerLeaderboardManager sets the next/current opponent from the friendly player's
    ///   NEXT_OPPONENT_PLAYER_ID only on scene load and in OnTurnChanged, so after a rewind across a combat the
    ///   leaderboard kept the later opponent;
    /// - the opposing NameBanner (top-left, "Bartender Bob" in the shop) lost its name until the next combat;
    /// - the leaderboard tiles keep the hero Entity they were made with, but a reset destroys every entity
    ///   (GameState.CleanGameState) and the snapshot makes new ones (OnRealTimeFullEntity: new Entity), so the
    ///   tiles read health and place from dead objects frozen at the first reset: at a game's end every hero
    ///   still showed alive. Each tile is given its new hero (ApplyEntityReplacement) and the layout redone.
    /// ResetGameSpellController ends with GameState.SetGameResetState(RESET_COMPLETE); a moment later we redo both
    /// the way the client itself does (OnTurnChanged's opponent logic minus combat-history replay; the
    /// SetName("") + UpdateHeroNameBanner() pattern mission scripts use). The turn rope is killed at once and the
    /// timer the server resent is fired after the turn start (RopeOnReplayClock).
    /// </summary>
    [HarmonyPatch(typeof(GameState), nameof(GameState.SetGameResetState))]
    internal static class AfterRewindFixups
    {
        private const float SettleSeconds = 0.3f;

        private static void Postfix(GameState.ResetGamePhase resetGamePhase)
        {
            if (resetGamePhase != GameState.ResetGamePhase.RESET_COMPLETE || !ReplaySpeed.InLocalGame) return;
            try { RopeOnReplayClock.KillRope(GameState.Get()); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Rope kill after rewind: " + e.Message); }
            Plugin.Instance.StartCoroutine(Apply());
        }

        /// <summary>The leaderboard's tiles given the snapshot's heroes (see the class note) the way a hero swap does
        /// it (ApplyEntityReplacement: the same tile, the new Entity), then the stock layout update (sort by place,
        /// health and armor bars, skull and dark portrait for 0 health). A jump back before a death makes the hero
        /// alive again, which a live game never does: its portrait, darkened by DarkenDeadHeroPortrait, is lit
        /// again and the stock update clears its skull.</summary>
        private static void RefreshLeaderboard(GameState gs)
        {
            var board = PlayerLeaderboardManager.Get();
            if (board == null) return;
            var tiles = board.GetAllTiles(); // the team containers are separate prefabs, not under the manager
            var report = new System.Text.StringBuilder();
            int rebound = 0;
            foreach (var tile in tiles)
            {
                var old = tile.Entity;
                if (old == null) continue;
                int playerId = old.GetTag(GAME_TAG.PLAYER_ID);
                var hero = gs.GetEntity(old.GetEntityId());
                if (hero == null && gs.GetPlayerInfoMap().TryGetValue(playerId, out var info)) hero = info?.GetPlayerHero();
                if (hero == null) { report.Append($" p{playerId}:none"); continue; }
                if (!ReferenceEquals(hero, old))
                {
                    board.ApplyEntityReplacement(playerId, hero);
                    rebound++;
                }
                report.Append($" p{playerId}:{hero.GetRealTimeRemainingHP()}hp#{hero.GetRealTimePlayerLeaderboardPlace()}");
            }
            // A stock layout started in the settle time slides the teams (1 s iTween) to where the old heroes put
            // them, over the positions set here.
            int slides = 0;
            foreach (var team in tiles.Select(t => t.Team).Where(t => t != null).Distinct())
            {
                slides += iTween.Count(team.gameObject);
                iTween.Stop(team.gameObject, "move");
            }
            board.UpdateLayout(animate: false);
            int lit = 0;
            foreach (var tile in tiles)
            {
                var hero = tile.Entity;
                if (hero == null || tile.Team == null || hero.GetRealTimeRemainingHP() <= 0 || tile.m_portraitMesh == null) continue;
                bool shares = Traverse.Create(tile).Field("m_portraitSharesFrameMesh").GetValue<bool>();
                var materials = tile.m_portraitMesh.materials; // the renderer's own instances, as the stock darkening uses
                var material = materials.Length > (shares ? 1 : 0) ? materials[shares ? 1 : 0] : null;
                if (material == null || material.color != tile.Team.m_deadColor) continue;
                material.color = shares ? Board.Get().m_HistoryTileColor : Color.white;
                lit++;
            }
            Plugin.Log.LogInfo($"After rewind: leaderboard {tiles.Count} tile(s), {rebound} given the snapshot's hero, " +
                               $"{lit} lit again, {slides} tween(s) stopped:{report}");
        }

        /// <summary>Diagnosing tiles drawn out of the sorted order: every leaderboard manager in the scene (which one
        /// Get() returns), and per team of Get()'s list: player, world z of the team and of its tile, the iTweens on
        /// the team, active.</summary>
        private static string Detail()
        {
            var managers = Resources.FindObjectsOfTypeAll<PlayerLeaderboardManager>().Where(o => o.gameObject.scene.IsValid()).ToArray();
            var board = PlayerLeaderboardManager.Get();
            var sb = new System.Text.StringBuilder();
            sb.Append($"{managers.Length} manager(s)");
            foreach (var m in managers)
                sb.Append($" [{m.GetInstanceID()}{(m == board ? " Get" : "")} active={m.isActiveAndEnabled}]");
            if (board == null) return sb.ToString();
            var teams = Traverse.Create(board).Field("m_teams").GetValue<System.Collections.Generic.List<PlayerLeaderboardTeam>>();
            sb.Append($"; {teams?.Count ?? 0} team(s) in list order:");
            if (teams != null)
                foreach (var team in teams)
                {
                    var tile = board.GetAllTiles().FirstOrDefault(t => t.Team == team);
                    sb.Append($" p{tile?.Entity?.GetTag(GAME_TAG.PLAYER_ID)} team#{team.GetInstanceID()} z={team.transform.position.z:0.000}" +
                              $" tile z={(tile != null ? tile.transform.position.z : float.NaN):0.000}" +
                              $" tweens={iTween.Count(team.gameObject)}/{team.GetComponents<iTween>().Length}" +
                              $"{(team.gameObject.activeInHierarchy ? "" : " inactive")};");
                }
            var allTeams = Resources.FindObjectsOfTypeAll<PlayerLeaderboardTeam>().Where(o => o.gameObject.scene.IsValid()).ToArray();
            sb.Append($" teams in scene {allTeams.Length}");
            return sb.ToString();
        }

        /// <summary>The leaderboard top to bottom as drawn: player, place, health (for the log).</summary>
        internal static string DrawnOrder()
        {
            var board = PlayerLeaderboardManager.Get();
            if (board == null) return "none";
            return string.Join(" ", board.GetAllTiles()
                .Where(t => t.Entity != null && t.Team != null)
                .OrderByDescending(t => t.Team.transform.position.z)
                .Select(t => $"p{t.Entity.GetTag(GAME_TAG.PLAYER_ID)}#{t.Entity.GetRealTimePlayerLeaderboardPlace()}:{t.Entity.GetRealTimeRemainingHP()}hp"));
        }

        private static IEnumerator LogDrawnOrderLater()
        {
            yield return new WaitForSecondsRealtime(2f);
            try
            {
                Plugin.Log.LogInfo("Leaderboard 2 s after the rewind, top to bottom: " + DrawnOrder());
                Plugin.Log.LogInfo("Leaderboard detail: " + Detail());
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Leaderboard order: " + e.Message); }
        }

        private static IEnumerator Apply()
        {
            yield return new WaitForSecondsRealtime(SettleSeconds);
            var gs = GameState.Get();
            var friendly = gs?.GetFriendlySidePlayer();
            if (friendly == null) yield break;

            // The BG script's phase: a reset makes a new TB_BaconShop in the shop phase (m_gamePhase 1), and only a
            // mission event switches it (2 = combat). A jump into a combat brings no such event, so the whole combat
            // ran as "shop": IsBattlePhase() false, the leaderboard's swords hidden (UpdateSwordsDisplay).
            try
            {
                var bacon = gs.GetGameEntity() as TB_BaconShop;
                if (bacon != null && gs.GetGameEntity().GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) == 1 && !bacon.IsBattlePhase())
                {
                    Traverse.Create(bacon).Field("m_gamePhase").SetValue(2);
                    Plugin.Log.LogInfo("After rewind: in a combat, the BG phase set to combat");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("BG phase after rewind: " + e.Message); }

            // The tiles first: their layout update hides every swords icon (UpdateLayout -> HideAllSwordsDisplay), so
            // opponents set before it were wiped (seen 2026-10-16: a jump into a combat with Genn left no swords and
            // the pop-out on the tile that was next before the jump).
            try { RefreshLeaderboard(gs); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Leaderboard re-read after rewind: " + e.Message); }

            try
            {
                var board = PlayerLeaderboardManager.Get();
                if (board != null)
                {
                    int opponent = friendly.GetTag(GAME_TAG.NEXT_OPPONENT_PLAYER_ID);
                    var current = gs.GetCurrentPlayer();
                    bool shop = current != null && current.IsFriendlySide();
                    // The pop-out marks the next opponent. A live game sets it in the shop and keeps it through
                    // the combat with that player; OnTurnChanged's combat branch only adds the swords. After a jump
                    // into a combat it must be set too, or the tile that was next before the jump stays out.
                    board.SetNextOpponent(opponent);
                    board.SetCurrentOpponent(shop ? -1 : opponent);
                    board.UpdateSwordsDisplay(); // the stock combat swords, from BACON_CURRENT_COMBAT_PLAYER_ID
                    Plugin.Log.LogInfo($"After rewind: opponent p{opponent}, " +
                                       (shop ? "next (in the shop)" : "current (in a combat)"));
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Leaderboard refresh after rewind: " + e.Message); }
            Plugin.Instance.StartCoroutine(LogDrawnOrderLater());

            try
            {
                var banner = Gameplay.Get()?.GetNameBannerForSide(Player.Side.OPPOSING);
                var opposing = gs.GetOpposingSidePlayer();
                if (banner != null && opposing?.GetHero() != null)
                {
                    banner.SetName("");
                    banner.UpdateHeroNameBanner();
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Name banner refresh after rewind: " + e.Message); }

            try { FinishTurnStart(gs); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Turn start after rewind: " + e.Message); }

            // The turn timer the server resent with the snapshot (held while the reset was queued), on the new board.
            try { RopeOnReplayClock.RefireTimer(gs); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Timer after rewind: " + e.Message); }
        }

        /// <summary>
        /// A reset into the friendly player's shop applies CURRENT_PLAYER 0 -> 1, which makes TurnStartManager listen
        /// for turn events (Player.OnTagChanged), but the snapshot already holds NEXT_STEP = MAIN_ACTION, so the
        /// 9 -> 10 change that plays them (MissionEntity.OnTagChanged) never comes. The manager then listens forever:
        /// gold changes queue behind it (stale counter), input and option highlights stay blocked, and the rope
        /// waits on a FireFriendlyTurnStartedEvent that never fires. Play the turn events ourselves, without the
        /// "your turn" banner.
        /// </summary>
        private static void FinishTurnStart(GameState gs)
        {
            var tsm = TurnStartManager.Get();
            var game = gs.GetGameEntity();
            if (tsm == null || game == null) return;
            int step = game.GetTag(GAME_TAG.STEP), nextStep = game.GetTag(GAME_TAG.NEXT_STEP);
            bool listening = tsm.IsListeningForTurnEvents(), ourTurn = gs.IsLocalSidePlayerTurn();
            Plugin.Log.LogInfo($"After rewind: turn start listening={listening} blocking={tsm.IsBlockingInput()} " +
                               $"ourTurn={ourTurn} step={step} nextStep={nextStep}");
            if (!listening || !ourTurn || step != (int)TAG_STEP.MAIN_ACTION) return;
            TwoScoopsDisplayed(tsm) = true;
            tsm.BeginPlayingTurnEvents();
        }

        private static readonly AccessTools.FieldRef<TurnStartManager, bool> TwoScoopsDisplayed =
            AccessTools.FieldRefAccess<TurnStartManager, bool>("m_twoScoopsDisplayed");
    }

    /// <summary>Local replay: each stock leaderboard layout, with the order it leaves (diagnosing the order after
    /// jumps).</summary>
    [HarmonyPatch(typeof(PlayerLeaderboardManager), nameof(PlayerLeaderboardManager.UpdateLayout))]
    internal static class LogLeaderboardLayout
    {
        private static void Postfix(bool animate)
        {
            if (!ReplaySpeed.InLocalGame) return;
            try
            {
                var caller = new System.Diagnostics.StackFrame(2).GetMethod();
                Plugin.Log.LogInfo($"Leaderboard UpdateLayout(animate {animate}) from {caller?.DeclaringType?.Name}.{caller?.Name}: " +
                                   AfterRewindFixups.DrawnOrder());
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Leaderboard layout log: " + e.Message); }
        }
    }
}
