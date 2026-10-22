using HarmonyLib;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Recorder: note when the client starts showing each top-level block of a Battlegrounds combat.
    ///
    /// The server sends a whole fight in one packet; the client then animates it block by block for 10-30 s. A
    /// replay splits the fight into its visible blocks (hsserver/combatblocks.py) and puts each on the timeline;
    /// these notes give the moment the original client began each one, so the ticks sit where the animation is.
    /// Written as {"kind":"note","text":"block TYPE SOURCE TARGET"} (HistoryBlock.Type and entity ids, as the
    /// PowerStart had them), only in real games and only while the executed BACON_IN_COMBAT_PHASE is 1. Every
    /// top-level block is noted (also the engine's); the server matches them to its blocks by type and source.
    /// In a replay the same block starts end a paused walk through a combat (ReplaySpeed.OnCombatBlockStart).
    /// </summary>
    [HarmonyPatch(typeof(PowerProcessor), "StartCurrentTaskList")]
    internal static class CombatBlockNote
    {
        public const string Text = "block";
        private static readonly AccessTools.FieldRef<PowerProcessor, PowerTaskList> Current =
            AccessTools.FieldRefAccess<PowerProcessor, PowerTaskList>("m_currentTaskList");

        private static void Prefix(PowerProcessor __instance)
        {
            try
            {
                var network = global::Network.Get();
                if (network == null || !network.IsConnectedToGameServer()) return;
                var list = Current(__instance);
                if (list == null || !list.IsStartOfBlock() || list.GetParent() != null || list.IsSubSpellTaskList()) return;
                if (GameState.Get()?.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) != 1) return;
                var start = list.GetBlockStart();
                if (start == null) return;
                if (LocalServer.Active)
                {
                    ReplaySpeed.OnCombatBlockStart(start); // a replay: walking a combat while paused
                    return;
                }
                int source = start.Entities != null && start.Entities.Count > 0 ? start.Entities[0] : 0;
                PacketRecorder.Note($"{Text} {(int)start.BlockType} {source} {start.Target}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Combat block note: " + e.Message); }
        }
    }
}
