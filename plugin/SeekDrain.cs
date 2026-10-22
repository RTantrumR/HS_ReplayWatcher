using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PegasusGame;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replay: when a jump snapshot (a GAME_RESET block) arrives while the client is still playing earlier
    /// power tasks — a 20 s combat, or the rebuild of a previous jump — race through them instead of waiting.
    ///
    /// The client has no queue flush, and dropping queued/in-flight task lists corrupts the board in several
    /// ways (stale controller coroutines and zone-change lists end or re-run the new reset). Draining keeps
    /// every task in order and only drops the visuals:
    /// - each SpellController (attack, death, trigger, play…) is finished at once, the same way the client's own
    ///   lost-frame catch-up does it (SpellController.DoPowerTaskList: OnFinishedTaskList(); OnFinished()), so
    ///   its task list is applied without the animation;
    /// - Time.timeScale is raised (<see cref="ReplaySpeed.AnimationFactor"/>) so zone tweens and waits finish
    ///   within a few frames.
    /// Draining ends when the newest reset's own task list becomes current (its ResetGameSpellController then
    /// plays normally, with the wipe) or nothing is left to process; after <see cref="DrainLimitSeconds"/> it gives up.
    ///
    /// Stale resets: a snapshot that arrives while an earlier snapshot is still queued (clicks faster than the
    /// server settles them) makes that earlier one, and every live task list queued after it, a timeline nobody
    /// will see. Their tasks are marked completed on arrival of the newer one, so when they become current their
    /// controllers have nothing to do and end at once (DoTasks skips completed tasks; the controller's visuals are
    /// skipped by the drain). Finishing their controllers early would not do: OnFinishedTaskList makes the
    /// processor run the list's tasks, i.e. the whole stale rebuild. The stale snapshot's real-time ResetGame
    /// (run on receipt, it parks every later real-time task until the rebuild) is withdrawn too, else the newer
    /// snapshot's own would be parked behind a rebuild that never comes and the input gate stay shut.
    ///
    /// Choices from the timeline being left are dropped when the snapshot arrives; the server resends whichever
    /// choice is open at the jump's target right after it. An open choice (discover, trinket...) blocks the queue
    /// outright: GameState.MustWaitForChoices holds every later task list until the player answers, so a snapshot
    /// sent while one was on screen never ran. And the reset (CleanGameState) keeps GameState's own choice
    /// bookkeeping: an old choice left in m_choicesMap made the resent one with the same id wait in
    /// m_queuedChoices; the recorded pick then closed the old one and the queued copy opened with no pick left to
    /// close it, holding the queue (visuals lagging the replay) until the next jump. So both are cleared: the
    /// choice on screen the way the client cancels choices itself (ChoiceCardMgr.CancelChoices), and GameState's
    /// open choices, queued choice packets and chosen entities.
    /// </summary>
    [HarmonyPatch(typeof(GameState), nameof(GameState.OnPowerHistory))]
    internal static class SeekDrain
    {
        public const float DrainTimeScale = 50f;
        private const float DrainLimitSeconds = 5f;

        public static bool Draining { get; private set; }
        private static int _skipped;
        private static float _drainStarted;
        private static readonly MethodInfo CancelChoices = AccessTools.Method(typeof(ChoiceCardMgr), "CancelChoices");

        private static readonly MethodInfo FinishedTaskList = AccessTools.Method(typeof(SpellController), "OnFinishedTaskList");
        private static readonly MethodInfo Finished = AccessTools.Method(typeof(SpellController), "OnFinished");

        private static void Prefix(GameState __instance, List<Network.PowerHistory> powerList)
        {
            if (!ReplaySpeed.InLocalGame || !ContainsGameReset(powerList)) return;
            CloseOldBlocks(__instance);
            DropOldChoices(__instance);
            try { SkipStaleResets(__instance); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Jump snapshot: could not withdraw the stale snapshot: " + e); }
            if (Draining || !__instance.HasPowersToProcess()) return;
            var queued = __instance.GetPowerProcessor()?.GetPowerQueue()?.Count ?? 0;
            Plugin.Log.LogInfo($"Jump snapshot arrived with {queued} task list(s) still queued: draining");
            _skipped = 0;
            _drainStarted = Time.unscaledTime;
            Draining = true;
            ReplaySpeed.RefreshTimeScale();
        }

        /// <summary>A newer snapshot is arriving: every queued (not yet current) GAME_RESET list and everything
        /// queued after it belongs to a timeline that will never be shown. See the class summary.</summary>
        private static void SkipStaleResets(GameState gs)
        {
            var pp = gs.GetPowerProcessor();
            var queue = pp?.GetPowerQueue();
            if (queue == null) return;
            int first = -1;
            for (int i = 0; i < queue.Count && first < 0; i++)
                if (queue[i].GetBlockType() == HistoryBlock.Type.GAME_RESET) first = i;
            if (first < 0) return;
            var stale = new HashSet<PowerTask>();
            int lists = 0;
            for (int i = first; i < queue.Count; i++, lists++)
                foreach (var task in queue[i].GetTaskList())
                {
                    stale.Add(task);
                    if (!task.IsCompleted()) task.SetCompleted(true);
                }
            // The stale snapshot's real-time ResetGame, if it ran on receipt, is what the processor waits on.
            var t = Traverse.Create(gs);
            var pending = t.Field("m_realTimeResetGame").GetValue<Network.HistResetGame>();
            bool withdrawn = false;
            if (pending != null)
                foreach (var task in stale)
                    if (ReferenceEquals(task.GetPower(), pending))
                    {
                        gs.SetResetGameToNull();
                        withdrawn = true;
                        break;
                    }
            // Real-time tasks of the stale timeline still parked behind that rebuild are dropped; others kept in order.
            var delayed = Traverse.Create(pp).Field("m_delayedRealTimeTasks");
            var kept = new List<object>();
            int dropped = 0;
            foreach (object entry in delayed.GetValue<System.Collections.IEnumerable>())
            {
                var task = Traverse.Create(entry).Field("m_powerTask").GetValue<PowerTask>();
                if (task != null && stale.Contains(task)) dropped++;
                else kept.Add(entry);
            }
            if (dropped > 0)
            {
                delayed.Method("Clear").GetValue();
                foreach (object entry in kept) delayed.Method("Enqueue", entry).GetValue();
            }
            if (withdrawn) pp.FlushDelayedRealTimeTasks();
            Plugin.Log.LogInfo($"Jump snapshot: a newer one arrived; {lists} stale task list(s) ({stale.Count} tasks) will be skipped" +
                               $"{(withdrawn ? ", the stale real-time reset withdrawn" : "")}{(dropped > 0 ? $", {dropped} parked real-time task(s) dropped" : "")}");
        }

        private static readonly AccessTools.FieldRef<PowerProcessor, Stack<PowerTaskList>> OpenBlocks =
            AccessTools.FieldRefAccess<PowerProcessor, Stack<PowerTaskList>>("m_previousStack");
        private static readonly AccessTools.FieldRef<PowerProcessor, Stack<PowerTaskList>> SubSpellOrigins =
            AccessTools.FieldRefAccess<PowerProcessor, Stack<PowerTaskList>>("m_subSpellOriginStack");
        private static readonly AccessTools.FieldRef<PowerProcessor, Stack<List<PowerTaskList>>> Deferred =
            AccessTools.FieldRefAccess<PowerProcessor, Stack<List<PowerTaskList>>>("m_deferredStack");
        private static readonly MethodInfo EnqueueDeferred = AccessTools.Method(typeof(PowerProcessor), "EnqueueDeferredTaskLists");

        /// <summary>The processor parses packets on top of the blocks still open from earlier ones (m_previousStack),
        /// and the recorded stream leaves blocks open across packets (a combat's end opens a trigger the next shop
        /// packets close). A snapshot arriving then would be parsed as their child, and so would everything after
        /// it: a fight's attacks are no longer top-level blocks, so walks and the pause never see them. The old
        /// timeline's blocks will never be closed (the server goes on from the snapshot), so they are closed here:
        /// the snapshot starts at the top level. Deferred lists of those blocks are queued (the drain plays them).</summary>
        private static void CloseOldBlocks(GameState gs)
        {
            var pp = gs.GetPowerProcessor();
            if (pp == null) return;
            var open = OpenBlocks(pp);
            if (open == null || open.Count == 0) return;
            int count = open.Count;
            open.Clear();
            SubSpellOrigins(pp)?.Clear();
            var deferred = Deferred(pp);
            while (deferred != null && deferred.Count > 1) EnqueueDeferred.Invoke(pp, new object[] { false });
            Plugin.Log.LogInfo($"Jump snapshot: {count} block(s) of the old timeline were still open; closed so the snapshot starts at the top level");
        }

        private static void DropOldChoices(GameState gs)
        {
            var choices = ChoiceCardMgr.Get();
            if (choices != null && choices.HasChoices())
            {
                try
                {
                    CancelChoices.Invoke(choices, null);
                    Plugin.Log.LogInfo("Jump snapshot: cancelled the choice on screen");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Jump snapshot: could not cancel the choice on screen: " + (e.InnerException?.Message ?? e.Message));
                }
            }
            try
            {
                var t = Traverse.Create(gs);
                int open = t.Field("m_choicesMap").Property("Count").GetValue<int>();
                int queued = t.Field("m_queuedChoices").Property("Count").GetValue<int>();
                if (open == 0 && queued == 0) return;
                t.Field("m_choicesMap").Method("Clear").GetValue();
                t.Field("m_queuedChoices").Method("Clear").GetValue();
                t.Field("m_chosenEntities").Method("Clear").GetValue();
                Plugin.Log.LogInfo($"Jump snapshot: dropped {open} open and {queued} queued choice packet(s) of the old timeline");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Jump snapshot: could not clear GameState's choices: " + e.Message);
            }
        }

        private static bool ContainsGameReset(List<Network.PowerHistory> powerList)
        {
            if (powerList == null) return false;
            foreach (var p in powerList)
                if (p is Network.HistBlockStart b && b.BlockType == HistoryBlock.Type.GAME_RESET) return true;
            return false;
        }

        /// <summary>The newest snapshot's task list is being processed (no later GAME_RESET waits in the queue).</summary>
        private static bool ResetIsCurrent()
        {
            var pp = GameState.Get()?.GetPowerProcessor();
            var current = pp?.GetCurrentTaskList();
            if (current == null || current.GetBlockType() != HistoryBlock.Type.GAME_RESET) return false;
            var queue = pp.GetPowerQueue();
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (queue[i].GetBlockType() == HistoryBlock.Type.GAME_RESET) return false;
            return true;
        }

        /// <summary>Per frame from Plugin.Update: stop once the newest snapshot is being processed.</summary>
        public static void Update()
        {
            if (!Draining) return;
            var gs = GameState.Get();
            bool timedOut = Time.unscaledTime - _drainStarted > DrainLimitSeconds;
            bool done = gs == null || ResetIsCurrent() || !gs.HasPowersToProcess();
            if (!ReplaySpeed.InLocalGame || done || timedOut)
            {
                if (timedOut)
                    Plugin.Log.LogWarning($"Drain gave up after {DrainLimitSeconds:0} s (skipped {_skipped} spell controller(s)); " +
                                          $"queue {gs?.GetPowerProcessor()?.GetPowerQueue()?.Count}, current {gs?.GetPowerProcessor()?.GetCurrentTaskList()?.GetBlockType()}");
                else
                    Plugin.Log.LogInfo($"Drain done: skipped {_skipped} spell controller(s)");
                Draining = false;
                ReplaySpeed.RefreshTimeScale();
            }
        }

        /// <summary>
        /// The client's stuck detector (GameState.CheckReconnectIfStuck): a spell or controller registered as
        /// server-blocking for DisconnectWhenStuckSeconds (25 s) makes it disconnect to reconnect — which ends a
        /// replay (seen: three play-spawn spells left registered after a jump). The method runs every frame the
        /// client is blocked and holds the threshold itself, so it is kept here; past it the blockers are dropped and
        /// logged instead; the replay goes on.
        /// </summary>
        [HarmonyPatch(typeof(GameState), "ReconnectIfStuck")]
        private static class NoStuckReconnect
        {
            private const float StuckSeconds = 25f; // the stock limit (a setup saying 0 would reconnect at once)

            private static bool Prefix(GameState __instance, ref bool __result)
            {
                if (!ReplaySpeed.InLocalGame) return true;
                var t = Traverse.Create(__instance);
                // Called every frame the client is blocked; the stock method's own threshold comes first.
                float stuckFor = t.Field("m_reconnectIfStuckTimer").GetValue<float>();
                uint limit = 0;
                try { limit = GameMgr.Get()?.GetGameSetup()?.DisconnectWhenStuckSeconds ?? 0u; } catch { }
                if (stuckFor < Mathf.Max(limit, StuckSeconds)) { __result = false; return false; }
                string cause = "?";
                try { cause = t.Method("BuildServerBlockingCausesString").GetValue<string>(); } catch { }
                var spells = t.Field("m_serverBlockingSpells").GetValue<List<Spell>>();
                var controllers = t.Field("m_serverBlockingSpellControllers").GetValue<List<SpellController>>();
                Plugin.Log.LogWarning($"Replay: the client was stuck for {stuckFor:0} s on {spells?.Count} spell(s), {controllers?.Count} controller(s) and " +
                                      $"would reconnect; dropped instead. Cause: {cause}");
                spells?.Clear();
                controllers?.Clear();
                t.Field("m_reconnectIfStuckTimer").SetValue(0f);
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(SpellController), nameof(SpellController.DoPowerTaskList))]
        private static class SkipControllerVisuals
        {
            private static bool Prefix(SpellController __instance)
            {
                // A stale snapshot's controller (its tasks already marked completed) is finished at once whether or
                // not a drain is on: run, it would play the curtain and report a reset that never happened.
                bool stale = __instance is ResetGameSpellController && (__instance.GetPowerTaskList()?.AreTasksComplete() ?? false);
                if (!stale && (!Draining || ResetIsCurrent())) return true;
                try
                {
                    FinishedTaskList.Invoke(__instance, null); // virtual: subclasses' overrides run, as in catch-up
                    Finished.Invoke(__instance, null);
                    if (stale) Plugin.Log.LogInfo("Stale reset skipped");
                    else _skipped++;
                    return false;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Drain: could not skip {__instance.GetType().Name}: {e.InnerException?.Message ?? e.Message}");
                    return true;
                }
            }
        }
    }
}
