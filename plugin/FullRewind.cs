using HarmonyLib;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replay: make the server's rewind snapshot rebuild the whole board.
    ///
    /// A GAME_RESET block whose ResetGame has SourceIsRewind goes to RewindGameSpellController — the constructed
    /// REWIND keyword's light rewind. It drops the ResetGame task (so the CreateGame and the player entities, i.e.
    /// gold, are never applied), only diffs a fixed list of "visual" tags, and sets ZONE/CONTROLLER silently, so
    /// cards don't move between hand and shop. Without the flag the block goes to ResetGameSpellController, which
    /// runs GameState.OnResetGame (CleanGameState + recreate game entity and players) and recreates every entity
    /// from the FullEntity list — and that controller still plays the rewind screen wipe when the ResetGame
    /// carries SourceIsRewind. Telling the router "not a rewind" therefore gets the full rebuild with the rewind
    /// visuals.
    /// </summary>
    [HarmonyPatch(typeof(PowerProcessor), "IsResetSourceRewind")]
    internal static class FullRewind
    {
        private static bool Prefix(ref bool __result)
        {
            if (!ReplaySpeed.InLocalGame) return true;
            __result = false;
            return false;
        }
    }
}
