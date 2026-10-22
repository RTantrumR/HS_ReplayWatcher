using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Blizzard.T5.Core.Time;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Keeps the client in step with the replay server's speed (0 = paused, 1..4) and position.
    ///
    /// - Animations: TimeScaleMgr.Update writes Time.timeScale = gameTimeScale * multiplier whenever HS changes
    ///   either; a postfix multiplies in <see cref="AnimationFactor"/> (the speed; a seek's drain, a reset's wipe
    ///   and the hero pick with a seek pending run faster).
    /// - Pause: the game's events are held, not time (<see cref="HoldsQueue"/>): the event already playing finishes
    ///   and the next one waits, while hovers, portraits and idle animations go on. Not held: a move made while
    ///   paused, which plays out until the client has nothing left to process (<see cref="MoveSettling"/>), and a
    ///   walk through a combat's blocks (<see cref="BlockWalk"/>).
    /// - Timers: turn/hero timers count down against Time.realtimeSinceStartup (wall clock). Their four reads are
    ///   swapped for <see cref="Now"/>. During a local replay Now is anchored to the server's virtual time (vt):
    ///   it advances at the replay speed between server replies and jumps with it (Enter), so timers freeze on
    ///   pause and skip what a jump skips. Outside a local replay Now runs at real time.
    /// </summary>
    internal static class ReplaySpeed
    {
        public static float Speed { get; private set; } = 1f;
        private static float _clock;
        private static bool _clockStarted;
        private static bool _anchored;
        private static float _anchor; // value of _clock at vt = 0

        private static readonly MethodInfo TimeScaleUpdate = AccessTools.Method(typeof(TimeScaleMgr), "Update");

        /// <summary>Replacement for Time.realtimeSinceStartup in the timer code.</summary>
        public static float Now
        {
            get
            {
                if (!_clockStarted) { _clock = Time.realtimeSinceStartup; _clockStarted = true; }
                return _clock;
            }
        }

        public static bool InLocalGame =>
            LocalServer.Active && global::Network.Get() != null && global::Network.Get().IsConnectedToGameServer();

        public static void Update()
        {
            if (!_clockStarted) { _clock = Time.realtimeSinceStartup; _clockStarted = true; }
            bool local = InLocalGame;
            // The server's clock stands still while the client animates a combat; so does this one.
            _clock += Time.unscaledDeltaTime * (!local ? 1f : ReplayControl.CombatPlaying ? 0f : Speed);
            if (!local)
            {
                _anchored = false;
                MoveSettling = false;
                BlockWalk = 0;
                Rewinding = false;
                _heldAt = -1;
                if (Speed != 1f) Set(1f); // game over / disconnected / local mode off
            }
            else
            {
                UpdateMoveSettling();
                UpdateRewinding();
            }
            // TimeScaleMgr rewrites the time scale only when HS changes its own inputs: rewrite it when ours change.
            if (_actionBoostUntil >= 0f && !ActionBoosting) _actionBoostUntil = -1f;
            float factor = AnimationFactor;
            if (factor != _appliedFactor)
            {
                _appliedFactor = factor;
                RefreshTimeScale();
            }
        }

        private static float _appliedFactor = 1f;

        // ------------------------------------------------------------------ a move made while paused

        /// <summary>A move (step, seek, jump) was made while paused and its packets are still playing: they play
        /// (a step watched event by event), then the pause holds the client's events again.</summary>
        public static bool MoveSettling { get; private set; }
        private static float _moveAt, _idleSince = -1f;
        // The answer to a move arrives within this (a jump settles 0.15 s on the server, its snapshot takes up to
        // ~0.5 s to build); the client then counts as done once nothing is left to process for IdleSeconds.
        private const float MoveAnswerSeconds = 1f, IdleSeconds = 0.3f, MoveSettleLimitSeconds = 15f;

        /// <summary>The replay bar or a key moved the replay (ReplayTimeline / ReplayControl). walk: a forward step
        /// of that many events; paused in a combat, it walks the fight block by block (see BlockWalk), assumed until
        /// the server's answer says otherwise (OnMoveReply), so no block start is missed meanwhile.</summary>
        public static void NoteMove(int walk = 0)
        {
            if (!InLocalGame) return;
            _moveAt = Time.unscaledTime;
            _idleSince = -1f;
            if (walk > 0 && Speed < 1f && (GameState.Get()?.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) ?? 0) == 1)
            {
                BlockWalk = _walkAssumed = walk;
                MoveSettling = false;
                return;
            }
            BlockWalk = _walkAssumed = 0;
            MoveSettling = true;
        }

        // ------------------------------------------------------------------ walking a combat while paused

        /// <summary>Paused in a combat, a step forward lets the client start this many more of the fight's visible
        /// blocks (hsserver/combatblocks.py), then holds it before the next: each step plays one block. The server
        /// moves its position without a jump (walk=n).</summary>
        public static int BlockWalk { get; private set; }
        private static int _walkAssumed; // blocks a walk was assumed for before the server answered (NoteMove)

        /// <summary>The server's answer to a move (mv=1): walk = blocks it walked (0: it moved otherwise). A fight the
        /// server held back before a block (a paused jump to a block) arrives with that block first: the walk starts
        /// it, as it starts a block held here.</summary>
        public static void OnMoveReply(int walk)
        {
            if (!InLocalGame || Speed >= 1f) return;
            if (walk > 0)
            {
                // Blocks may have started since the walk was assumed: keep that count, less what the server didn't
                // walk (the fight's last blocks).
                BlockWalk = _walkAssumed > 0 ? Mathf.Max(0, BlockWalk - (_walkAssumed - walk)) : walk;
                _walkAssumed = 0;
                MoveSettling = false;
            }
            else if (_walkAssumed > 0)
            {
                BlockWalk = _walkAssumed = 0; // it was another move: played on until the client is idle
                MoveSettling = true;
            }
        }

        /// <summary>A top-level block starts during a combat in a local replay (CombatBlockNote): count it off a walk
        /// when it is one of the fight's visible blocks (as the server's: an attack, a death phase, or a card's).</summary>
        public static void OnCombatBlockStart(Network.HistBlockStart start)
        {
            // A jump's drain races the old queue through, and a reset rebuilds: neither is the fight being shown.
            if (start == null || SeekDrain.Draining || (Rewinding && !_resetComplete)) return;
            if (!IsVisibleBlock(start)) return;
            // The server follows the fight by these (its position, the bar, the next walk).
            ReplayControl.SendSilent($"blockat {(int)start.BlockType} {(start.Entities?.Count > 0 ? start.Entities[0] : 0)}");
            if (BlockWalk <= 0) return;
            BlockWalk--;
            if (BlockWalk == 0) Plugin.Log.LogInfo($"Replay walk: played a {start.BlockType} block; the next one waits");
        }

        /// <summary>One of a fight's visible blocks, as the server counts them: an attack, a death phase, or a card's.</summary>
        private static bool IsVisibleBlock(Network.HistBlockStart start)
        {
            if (start.BlockType == PegasusGame.HistoryBlock.Type.ATTACK || start.BlockType == PegasusGame.HistoryBlock.Type.DEATHS) return true;
            if (start.Entities == null || start.Entities.Count == 0) return false;
            var source = GameState.Get()?.GetEntity(start.Entities[0]);
            var type = source?.GetCardType() ?? TAG_CARDTYPE.INVALID;
            return source != null && !string.IsNullOrEmpty(source.GetCardId())
                   && type != TAG_CARDTYPE.GAME && type != TAG_CARDTYPE.PLAYER && type != TAG_CARDTYPE.ENCHANTMENT;
        }

        // ------------------------------------------------------------------ the watched player's own actions

        /// <summary>The player's own client moves a card the moment it is dropped (InputManager adds a local zone
        /// change), so the server's answer has little left to animate; a spectator plays the card's whole travel
        /// from hand or shop: ~0.5 s a play, ~0.3-0.5 s a buy or sell, which adds up to 1-2 s behind the original in
        /// a burst of actions (measured, docs/replay.md). The first ActionBoostSeconds of game time of such an
        /// action run ActionBoost times faster. Its battlecries and triggers are blocks of their own after it.</summary>
        public static bool ActionBoosting => Time.time < _actionBoostUntil;
        private static float _actionBoostUntil = -1f;
        private const string ShopEnchant = "TB_BaconShop_8P_PlayerE"; // the shop's engine (buys, sells, refreshes)

        internal static void OnTaskListStart(PowerTaskList list)
        {
            float boost = Plugin.ActionBoost.Value;
            if (boost <= 1f || !InLocalGame || list == null || !list.IsStartOfBlock() || !OpenBlocks.IsTopLevel(list)
                || list.IsSubSpellTaskList() || SeekDrain.Draining || Rewinding) return;
            var gs = GameState.Get();
            if (gs == null || (gs.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) ?? 0) == 1) return;
            var start = list.GetBlockStart();
            var source = start?.Entities != null && start.Entities.Count > 0 ? gs.GetEntity(start.Entities[0]) : null;
            if (source == null) return;
            // Plays of cards and of the shop's drag buy/sell; not the refresh, upgrade and freeze buttons, which the
            // player's own client animated in full too.
            string id = source.GetCardId() ?? "";
            bool action = start.BlockType == PegasusGame.HistoryBlock.Type.PLAY
                          ? source.IsControlledByFriendlySidePlayer() && !id.EndsWith("_Button")
                        : start.BlockType == PegasusGame.HistoryBlock.Type.TRIGGER
                          && id.StartsWith(ShopEnchant) && MovesFriendlyCard(gs, list);
            if (!action) return;
            _actionBoostUntil = Time.time + Plugin.ActionBoostSeconds.Value;
            RefreshTimeScale();
            Plugin.Log.LogInfo($"Action boost: {start.BlockType} of {source.GetCardId()}");
        }

        /// <summary>A shop block that moves one of the watched player's cards (a buy into the hand, a sell); a refresh
        /// only moves Bob's.</summary>
        private static bool MovesFriendlyCard(GameState gs, PowerTaskList list)
        {
            int friendly = gs.GetFriendlySidePlayer()?.GetPlayerId() ?? -1;
            for (var l = list; l != null; l = l.GetNext())
                foreach (var task in l.GetTaskList())
                {
                    if (!(task.GetPower() is Network.HistTagChange change)) continue;
                    if (change.Tag == (int)GAME_TAG.CONTROLLER && change.Value == friendly) return true;
                    if (change.Tag == (int)GAME_TAG.ZONE && (gs.GetEntity(change.Entity)?.IsControlledByFriendlySidePlayer() ?? false))
                        return true;
                }
            return false;
        }

        // ------------------------------------------------------------------ the pause

        private static int _heldAt = -1; // the task list held last (logged once)

        /// <summary>Paused: the next task list waits if it begins a new event: a top-level list first in its chain
        /// (a block's start, or loose changes), in a combat only a visible block's start, so the attack playing
        /// finishes with its triggers. What is mid-way (a block's later lists, its sub-blocks) plays on.</summary>
        public static bool HoldsQueue(PowerTaskList next)
        {
            if (!InLocalGame || Speed >= 1f || next == null) return false;
            if (MoveSettling || BlockWalk > 0 || SeekDrain.Draining || Rewinding) return false;
            if (ReplayControl.PendingMs >= 0f && !ReplayControl.PastPickSent) return false;
            if (!OpenBlocks.IsTopLevel(next) || !next.IsOrigin() || next.IsSubSpellTaskList()) return false;
            if ((GameState.Get()?.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) ?? 0) == 1)
            {
                var start = next.IsStartOfBlock() ? next.GetBlockStart() : null;
                if (start == null || !IsVisibleBlock(start)) return false;
            }
            if (_heldAt != next.GetId())
            {
                _heldAt = next.GetId();
                Plugin.Log.LogInfo($"Replay paused: holding task list {next.GetId()} ({(next.IsStartOfBlock() ? next.GetBlockType().ToString() : "loose")})");
            }
            return true;
        }

        private static void UpdateMoveSettling()
        {
            if (!MoveSettling) return;
            float now = Time.unscaledTime;
            bool busy = now - _moveAt < MoveAnswerSeconds || Rewinding || SeekDrain.Draining
                        || (GameState.Get()?.GetPowerProcessor()?.HasTaskLists() ?? false);
            if (busy) _idleSince = -1f;
            else if (_idleSince < 0f) _idleSince = now;
            if ((_idleSince >= 0f && now - _idleSince >= IdleSeconds) || now - _moveAt > MoveSettleLimitSeconds)
                MoveSettling = false;
        }

        // ------------------------------------------------------------------ the rewind wipe

        /// <summary>A snapshot's reset is playing: from the ResetGameSpellController's RESETING (the wipe starts)
        /// until its cover screen has been taken away after RESET_COMPLETE. Played at RewindTimeScale.</summary>
        public static bool Rewinding { get; private set; }
        public const float RewindTimeScale = 3f;
        private static bool _resetComplete;
        private static float _resetStartedAt, _resetCompleteAt;
        private const float RevealLimitSeconds = 2f; // the cover's reveal after RESET_COMPLETE, at most (real time)
        private const float RewindLimitSeconds = 10f; // a reset that never completes (cut short) doesn't stay sped up

        private static readonly AccessTools.FieldRef<Spell> HideScreen =
            AccessTools.StaticFieldRefAccess<Spell>(AccessTools.Field(typeof(ResetGameSpellController), "s_hideScreenSpellInstance"));

        internal static void OnResetPhase(GameState.ResetGamePhase phase)
        {
            if (!InLocalGame) return;
            if (phase == GameState.ResetGamePhase.RESETING)
            {
                Rewinding = true;
                _resetComplete = false;
                _resetStartedAt = Time.unscaledTime;
            }
            else if (phase == GameState.ResetGamePhase.RESET_COMPLETE)
            {
                _resetComplete = true;
                _resetCompleteAt = Time.unscaledTime;
            }
        }

        private static void UpdateRewinding()
        {
            if (!Rewinding) return;
            if (Time.unscaledTime - _resetStartedAt > RewindLimitSeconds)
            {
                Plugin.Log.LogWarning("Rewind wipe: no RESET_COMPLETE within " + RewindLimitSeconds + " s; back to the replay speed");
                Rewinding = false;
                return;
            }
            if (!_resetComplete) return;
            // The controller releases its cover (the hide-screen loop) once the loop's reveal has played out.
            Spell cover = null;
            try { cover = HideScreen(); } catch (System.Exception e) { Plugin.Log.LogWarning("Rewind cover: " + e.Message); }
            if (cover == null || Time.unscaledTime - _resetCompleteAt > RevealLimitSeconds) Rewinding = false;
        }

        /// <summary>Apply a server status: speed and virtual time (ms since the recorded handshake).</summary>
        public static void OnServerStatus(float speed, float vtMs)
        {
            if (!_anchored)
            {
                _anchor = Now - vtMs / 1000f;
                _anchored = true;
            }
            else
            {
                float target = _anchor + vtMs / 1000f;
                if (Mathf.Abs(target - _clock) > 0.25f)
                    Plugin.Log.LogInfo($"Replay clock {(target > _clock ? "+" : "")}{target - _clock:0.0}s (vt {vtMs / 1000f:0.0}s)");
                _clock = target;
            }
            Set(speed);
        }

        public static void Set(float speed)
        {
            if (Mathf.Approximately(speed, Speed)) return;
            Speed = speed;
            Plugin.Log.LogInfo($"Replay speed -> {speed:0.##} (client timeScale x{AnimationFactor:0.##})");
            RefreshTimeScale();
        }

        /// <summary>Make TimeScaleMgr rewrite Time.timeScale now (it only does so when HS changes its inputs).</summary>
        public static void RefreshTimeScale()
        {
            try { TimeScaleUpdate?.Invoke(TimeScaleMgr.Get(), null); }
            catch (System.Exception e) { Plugin.Log.LogWarning("TimeScaleMgr.Update: " + e.Message); }
        }

        /// <summary>The hero screen can't be jumped past (see the replay server): a seek made there waits for it,
        /// so meanwhile the pick plays as at the top speed.</summary>
        public const float PickWithSeekPending = 4f;

        public static float AnimationFactor =>
            !InLocalGame ? 1f
            : SeekDrain.Draining ? SeekDrain.DrainTimeScale
            : Rewinding ? Mathf.Max(RewindTimeScale, Speed)
            : ReplayControl.PendingMs >= 0f && !ReplayControl.PastPickSent ? Mathf.Max(PickWithSeekPending, Speed)
            : (Speed >= 1f ? Speed : 1f) * (ActionBoosting ? Plugin.ActionBoost.Value : 1f);
    }

    /// <summary>
    /// The blocks the recorded stream leaves open across packets (a combat's end opens a TRIGGER and a POWER that
    /// later shop packets close; the processor keeps them on m_previousStack): every block of the packets after
    /// them is parsed as their child, though it is an event of its own. A list counts as top-level when its parent
    /// is none or one of those.
    /// </summary>
    [HarmonyPatch(typeof(PowerProcessor), nameof(PowerProcessor.OnPowerHistory))]
    internal static class OpenBlocks
    {
        private static readonly HashSet<PowerTaskList> Ambient = new HashSet<PowerTaskList>();
        private static readonly AccessTools.FieldRef<PowerProcessor, Stack<PowerTaskList>> Previous =
            AccessTools.FieldRefAccess<PowerProcessor, Stack<PowerTaskList>>("m_previousStack");

        public static bool IsTopLevel(PowerTaskList list)
        {
            var parent = list.GetParent();
            return parent == null || Ambient.Contains(parent);
        }

        private static void Postfix(PowerProcessor __instance)
        {
            if (!LocalServer.Active) { Ambient.Clear(); return; }
            var stack = Previous(__instance);
            if (stack == null) return;
            if (stack.Count == 0 && !__instance.HasTaskLists()) { Ambient.Clear(); return; }
            foreach (var list in stack) Ambient.Add(list.GetOrigin());
        }
    }

    /// <summary>Tells <see cref="ReplaySpeed"/> when the client starts a task list (the watched player's actions).</summary>
    [HarmonyPatch(typeof(PowerProcessor), "StartCurrentTaskList")]
    internal static class ActionBoostStart
    {
        private static readonly AccessTools.FieldRef<PowerProcessor, PowerTaskList> Current =
            AccessTools.FieldRefAccess<PowerProcessor, PowerTaskList>("m_currentTaskList");

        private static void Prefix(PowerProcessor __instance)
        {
            try { ReplaySpeed.OnTaskListStart(Current(__instance)); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Action boost: " + e.Message); }
        }
    }

    /// <summary>Tells <see cref="ReplaySpeed"/> when a reset's wipe starts and its rebuild is complete.</summary>
    [HarmonyPatch(typeof(GameState), nameof(GameState.SetGameResetState))]
    internal static class RewindWipeSpeed
    {
        private static void Postfix(GameState.ResetGamePhase resetGamePhase) => ReplaySpeed.OnResetPhase(resetGamePhase);
    }

    /// <summary>The pause: holds the client's next event (<see cref="ReplaySpeed.HoldsQueue"/>).</summary>
    [HarmonyPatch(typeof(GameState), nameof(GameState.CanProcessPowerQueue))]
    internal static class PauseHoldsEvents
    {
        private static void Postfix(GameState __instance, ref bool __result)
        {
            if (!__result) return;
            try
            {
                var queue = __instance.GetPowerProcessor()?.GetPowerQueue();
                if (queue != null && queue.Count > 0 && ReplaySpeed.HoldsQueue(queue.Peek())) __result = false;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Pause hold: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(TimeScaleMgr), "Update")]
    internal static class ScaleAnimations
    {
        private static void Postfix()
        {
            float f = ReplaySpeed.AnimationFactor;
            if (f != 1f) Time.timeScale *= f;
        }
    }

    [HarmonyPatch]
    internal static class TimersOnReplayClock
    {
        private static readonly MethodInfo Realtime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup));
        private static readonly MethodInfo ReplayNow = AccessTools.PropertyGetter(typeof(ReplaySpeed), nameof(ReplaySpeed.Now));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameState), "OnTurnTimerUpdate");
            yield return AccessTools.Method(typeof(GameState), "OnTurnChanged_TurnTimer");
            yield return AccessTools.Method(typeof(TurnTimer), "ComputeCountdownRemainingSec");
            yield return AccessTools.Method(typeof(MulliganTimer), "ComputeCountdownRemainingSec");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int swapped = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(Realtime))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = ReplayNow;
                    swapped++;
                }
                yield return ins;
            }
            if (swapped == 0)
                Plugin.Log.LogWarning($"Timer patch: no realtimeSinceStartup read found in {original.DeclaringType?.Name}.{original.Name}");
        }
    }
}
