namespace HsPuzzleBridge
{
    /// <summary>
    /// Recorder: note when the client has finished showing each Battlegrounds combat.
    ///
    /// The server sends a whole combat in under a second and starts the next shop right away; the client then
    /// animates the combat for 10-30 s, and only after that is the player really back in the shop. A replay
    /// needs that moment to resume the shop where the player saw it, instead of idling through the time the
    /// original client spent on the combat. It is the same edge the replay plugin reports as "combatdone": the
    /// executed (not real-time) BACON_IN_COMBAT_PHASE on the game entity going 1 -> 0. Written as a
    /// {"kind":"note","text":"combat_done"} line; only for real games (local replays are not the original).
    /// </summary>
    internal static class CombatDoneNote
    {
        public const string Text = "combat_done";
        private static int _lastPhase;

        public static void Update()
        {
            var network = global::Network.Get();
            bool realGame = network != null && network.IsConnectedToGameServer() && !LocalServer.Active;
            int phase = realGame ? GameState.Get()?.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) ?? 0 : 0;
            if (_lastPhase == 1 && phase == 0 && realGame) PacketRecorder.Note(Text);
            _lastPhase = phase;
        }
    }
}
