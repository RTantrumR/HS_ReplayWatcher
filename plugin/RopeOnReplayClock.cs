using System.Collections;
using System.Reflection;
using HarmonyLib;
using PegasusGame;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replay: make the turn rope follow the replay clock instead of the wall clock.
    ///
    /// Stock TurnTimer ignites the rope with WaitForSecondsRealtime(remaining - 20 s), scheduled once, and burns it
    /// with two iTweens that have ignoretimescale = true and last "remaining at ignition" — so it ignites during a
    /// pause and burns at real speed whatever the replay does. Here the ignition waits on
    /// ComputeCountdownRemainingSec() (already on the replay clock, see ReplaySpeed), the tweens are skipped, and
    /// <see cref="DriveRope"/> sets the rope from the remaining time every frame. A jump past ignition therefore
    /// shows a part-burned rope, and a pause freezes it.
    /// </summary>
    internal static class RopeOnReplayClock
    {
        private static readonly AccessTools.FieldRef<TurnTimer, float> CountdownTimeoutSec =
            AccessTools.FieldRefAccess<TurnTimer, float>("m_countdownTimeoutSec");
        private static readonly AccessTools.FieldRef<TurnTimer, int> LastTickSecondNumber =
            AccessTools.FieldRefAccess<TurnTimer, int>("m_lastTickSecondNumber");
        private static readonly AccessTools.FieldRef<TurnTimer, Coroutine> BelowCapCoroutine =
            AccessTools.FieldRefAccess<TurnTimer, Coroutine>("m_countdownAnimsWhenBelowCapCoroutine");
        private static readonly MethodInfo HandleTurnTimerUpdateAnims =
            AccessTools.Method(typeof(TurnTimer), "HandleTurnTimerUpdateAnims");
        private static readonly MethodInfo UpdateCountdownAnims =
            AccessTools.Method(typeof(TurnTimer), "UpdateCountdownAnims");

        /// <summary>Called every frame from the plugin.</summary>
        public static void DriveRope()
        {
            if (!ReplaySpeed.InLocalGame) return;
            var timer = TurnTimer.Get();
            if (timer == null || !timer.IsRopeActive() || UpdateCountdownAnims == null) return;
            // Burn progress = remaining / rope length, so a rope ignited late (after a jump) starts part-burned.
            CountdownTimeoutSec(timer) = timer.m_RopeCapSeconds;
            UpdateCountdownAnims.Invoke(timer, new object[] { timer.ComputeCountdownRemainingSec() });
        }

        internal static IEnumerator IgniteOnReplayClock(TurnTimer timer)
        {
            while (timer != null && timer.ComputeCountdownRemainingSec() > timer.m_RopeCapSeconds)
                yield return null;
            if (timer == null) yield break;
            float remaining = Mathf.Min(timer.ComputeCountdownRemainingSec(), timer.m_RopeCapSeconds);
            HandleTurnTimerUpdateAnims?.Invoke(timer, new object[] { remaining });
            BelowCapCoroutine(timer) = null;
        }

        internal static void ResetTickCounter(TurnTimer timer) =>
            LastTickSecondNumber(timer) = Mathf.CeilToInt(timer.m_RopeCapSeconds);

        // ------------------------------------------------------------------ across a jump

        private static readonly MethodInfo ChangeState = AccessTools.Method(typeof(TurnTimer), "ChangeState");
        private static Network.TurnTimerInfo _last; // the last timer the server sent, and the replay-clock time then
        private static float _lastAt;

        /// <summary>A snapshot's rebuild leaves the rope as it was (nothing in the client kills it on GAME_RESET), so the
        /// old turn's rope kept burning (or un-burning, backward) on the new board. Called at RESET_COMPLETE: the
        /// stock kill (the one a turn change does), and the parked timer updates dropped.</summary>
        public static void KillRope(GameState gs)
        {
            var timer = TurnTimer.Get();
            if (timer != null)
            {
                var state = timer.GetCurrentTurnTimerState();
                if (state == TurnTimerState.COUNTDOWN || state == TurnTimerState.START)
                {
                    var ignite = BelowCapCoroutine(timer);
                    if (ignite != null) { timer.StopCoroutine(ignite); BelowCapCoroutine(timer) = null; }
                    ChangeState?.Invoke(timer, new object[] { TurnTimerState.KILL });
                    Plugin.Log.LogInfo("After rewind: rope killed");
                }
            }
            try { Traverse.Create(gs).Field("m_turnTimerUpdates").Method("Clear").GetValue(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("After rewind: could not drop parked timer updates: " + e.Message); }
        }

        /// <summary>The timer the server resent with the snapshot arrived while the reset was still queued (it would
        /// have run against the old board), so it was held (<see cref="HoldTimerAcrossReset"/>); fire it now, less the
        /// replay time since, on the rebuilt board: GetTurn() is the snapshot's, so it triggers instead of parking.</summary>
        public static void RefireTimer(GameState gs)
        {
            if (_last == null) return;
            // The last timer of a turn is its end (0 s, sent as the combat starts): re-fired as "1 s left" it lit a
            // rope over the fight. A jump into a combat has no timer to show.
            if (_last.Seconds <= 0.5f)
            {
                Plugin.Log.LogInfo($"After rewind: no timer to re-fire (turn {_last.Turn}'s had run out: a combat)");
                return;
            }
            // Less the time the client took to get here (real time at the replay speed: the replay clock itself
            // jumps with the snapshot, so it can't be the measure; none while paused), never below a second.
            float late = Mathf.Max(0f, Time.unscaledTime - _lastAt) * ReplaySpeed.Speed;
            var info = new Network.TurnTimerInfo
            {
                Seconds = Mathf.Max(1f, _last.Seconds - late),
                Turn = _last.Turn, Show = _last.Show,
            };
            Plugin.Log.LogInfo($"After rewind: timer re-fired, {info.Seconds:0} s left on turn {info.Turn} (show {info.Show})");
            _refiring = true;
            try { gs.OnTurnTimerUpdate(info); }
            finally { _refiring = false; }
        }

        private static bool _refiring;

        /// <summary>Remember every timer the server sends; hold the ones that arrive while a snapshot is still to be
        /// applied (our own re-fire, made after the reset, passes: the reset's own task list may still be the
        /// current one then). Returns false to swallow.</summary>
        internal static bool HoldTimerAcrossReset(GameState gs, Network.TurnTimerInfo info)
        {
            if (_refiring) return true;
            _last = info;
            _lastAt = Time.unscaledTime;
            if (!gs.IsResetGamePending() && !ResetQueued(gs)) return true;
            Plugin.Log.LogInfo($"Timer for turn {info.Turn} ({info.Seconds:0} s) held until the snapshot is applied");
            return false;
        }

        private static bool ResetQueued(GameState gs)
        {
            var pp = gs.GetPowerProcessor();
            if (pp == null) return false;
            if (pp.GetCurrentTaskList()?.GetBlockType() == HistoryBlock.Type.GAME_RESET) return true;
            var queue = pp.GetPowerQueue();
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (queue[i].GetBlockType() == HistoryBlock.Type.GAME_RESET) return true;
            return false;
        }

        internal static void Forget() => _last = null;
    }

    [HarmonyPatch(typeof(GameState), nameof(GameState.OnTurnTimerUpdate), typeof(Network.TurnTimerInfo))]
    internal static class TimerAcrossReset
    {
        private static bool Prefix(GameState __instance, Network.TurnTimerInfo info)
        {
            if (!ReplaySpeed.InLocalGame) return true;
            return RopeOnReplayClock.HoldTimerAcrossReset(__instance, info);
        }
    }

    [HarmonyPatch(typeof(TurnTimer), "StartCountdownAnimsWhenBelowCapCoroutine")]
    internal static class RopeIgnition
    {
        private static bool Prefix(TurnTimer __instance, ref IEnumerator __result)
        {
            if (!ReplaySpeed.InLocalGame) return true;
            __result = RopeOnReplayClock.IgniteOnReplayClock(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(TurnTimer), "StartCountdownAnims")]
    internal static class RopeBurn
    {
        private static bool Prefix(TurnTimer __instance)
        {
            if (!ReplaySpeed.InLocalGame) return true;
            RopeOnReplayClock.ResetTickCounter(__instance); // the stock method resets the tick sounds here
            return false; // no real-time tweens; DriveRope positions the rope every frame
        }
    }
}
