using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// In-game keybinds for the local replay server: + / - change speed, Enter / Backspace step forward / back,
    /// Left / Right 10 seconds back / forward, Space pause.
    /// Steps are batched: each press adds one, and <see cref="BatchSeconds"/> after the last press a single
    /// "step <n>" goes out, so five quick Backspaces are one jump (one rebuild, one wipe), not five.
    /// Commands go to the server's UDP control port; it answers "speed=..;vt=..;step=..;total=..;combat=..;<text>".
    /// speed/vt drive <see cref="ReplaySpeed"/>; the text is shown with UIStatus for user commands only. A silent
    /// "status" is sent when a local game starts and every few seconds so the timer clock stays anchored.
    /// "points=..." replies (the replay's labelled points) go to <see cref="ReplayTimeline"/>.
    /// Only active during a replay (the client connected to the local server).
    /// </summary>
    internal static class ReplayControl
    {
        private const float SyncIntervalSeconds = 5f;
        public const float BatchSeconds = 0.35f;

        private static UdpClient _udp;
        private static readonly ConcurrentQueue<string> Replies = new ConcurrentQueue<string>();
        private static int _userCommandsAwaitingReply;
        private static bool _wasInLocalGame;
        private static int _lastCombatPhase;
        private static bool _pastPickSent;
        private static float _nextSync;
        private static int _pendingSteps;
        private static float _lastStepPress;

        /// <summary>Send a command now as a user command (its reply text is shown), e.g. a key.</summary>
        public static void SendUser(string cmd)
        {
            Interlocked.Increment(ref _userCommandsAwaitingReply);
            Send(cmd);
            _nextSync = Time.unscaledTime + SyncIntervalSeconds;
        }

        /// <summary>Add steps to the pending batch (Enter/Backspace).</summary>
        public static void QueueSteps(int n)
        {
            _pendingSteps += n;
            _lastStepPress = Time.unscaledTime;
            string text = _pendingSteps == 0 ? "Replay: no step"
                : $"Replay: {(_pendingSteps > 0 ? "forward" : "back")} {System.Math.Abs(_pendingSteps)} step{(System.Math.Abs(_pendingSteps) == 1 ? "" : "s")}";
            try { UIStatus.Get()?.AddInfo(text); } catch (Exception e) { Plugin.Log.LogWarning("UIStatus: " + e.Message); }
        }

        public static void Update()
        {
            bool inGame = ReplaySpeed.InLocalGame;
            if (inGame)
            {
                string cmd = null;
                if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus))
                    cmd = "faster";
                else if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus))
                    cmd = "slower";
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    QueueSteps(1);
                else if (Input.GetKeyDown(KeyCode.Backspace))
                    QueueSteps(-1);
                else if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ReplayTimeline.PreviousTurn();
                    else ReplayTimeline.PreviousEvent();
                }
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ReplayTimeline.NextTurn();
                    else ReplayTimeline.NextEvent();
                }
                else if (Input.GetKeyDown(KeyCode.Space))
                    ReplayTimeline.TogglePause();
                if (cmd == null && _pendingSteps != 0 && Time.unscaledTime - _lastStepPress >= BatchSeconds)
                {
                    cmd = "step " + _pendingSteps;
                    ReplaySpeed.NoteMove(_pendingSteps > 0 ? _pendingSteps : 0);
                    _pendingSteps = 0;
                }

                // The executed (not real-time) game-entity tag drops back to 0 only after every combat task has
                // played, which is when the server may release the next shop.
                int combatPhase = GameState.Get()?.GetGameEntity()?.GetTag(GAME_TAG.BACON_IN_COMBAT_PHASE) ?? 0;
                // Not while a jump's drain races the old queue through or a reset rebuilds: that ends the old fight,
                // not the one the server holds now.
                bool combatDone = _lastCombatPhase == 1 && combatPhase == 0 && !SeekDrain.Draining && !ReplaySpeed.Rewinding;
                _lastCombatPhase = combatPhase;

                // Once the hero screen is gone and the game runs, the server may jump by snapshot.
                if (!_pastPickSent)
                {
                    var gs = GameState.Get();
                    if (gs != null && gs.IsPastBeginPhase() && !gs.IsMulliganManagerActive())
                    {
                        _pastPickSent = true;
                        Send("pastpick");
                        if (PendingMs >= 0f) ReplaySpeed.RefreshTimeScale(); // the pick's speed-up ends
                    }
                }

                if (cmd != null)
                    SendUser(cmd);
                if (combatDone)
                {
                    Send("combatdone"); // silent; its reply re-anchors the timer clock as the shop starts
                    _nextSync = Time.unscaledTime + SyncIntervalSeconds;
                }
                else if (cmd == null && (!_wasInLocalGame || Time.unscaledTime >= _nextSync))
                {
                    Send("status");
                    _nextSync = Time.unscaledTime + SyncIntervalSeconds;
                }
            }
            else
            {
                _lastCombatPhase = 0;
                _pendingSteps = 0;
                _pastPickSent = false;
                PendingMs = -1f;
                RopeOnReplayClock.Forget();
            }
            _wasInLocalGame = inGame;

            while (Replies.TryDequeue(out string reply))
            {
                if (reply.StartsWith("points=", StringComparison.Ordinal))
                {
                    ReplayTimeline.OnPointsPage(reply);
                    continue;
                }
                if (reply.StartsWith("library=", StringComparison.Ordinal))
                {
                    ReplayLibrary.OnLibraryStatus(reply);
                    ReplayTimeline.OnLibraryStatus(reply);
                    continue;
                }
                string text = Apply(reply);
                if (text != null && Interlocked.Decrement(ref _userCommandsAwaitingReply) >= 0)
                {
                    try { UIStatus.Get()?.AddInfo(text); } catch (Exception e) { Plugin.Log.LogWarning("UIStatus: " + e.Message); }
                }
                else
                {
                    Interlocked.Exchange(ref _userCommandsAwaitingReply, 0);
                }
            }
        }

        /// <summary>Current replay position from the last server reply (step = points reached, 0..Total).</summary>
        public static int Step { get; private set; }
        public static int Total { get; private set; }
        public static bool CombatPlaying { get; private set; }
        /// <summary>The replay's place on the timeline (ms) as of the last reply, and when that reply came.</summary>
        public static float ShownMs { get; private set; }
        public static float ShownAt { get; private set; }
        /// <summary>The server is playing a reconnect (the recorded client dropped here): the clock stands still.</summary>
        public static bool Reconnecting { get; private set; }
        /// <summary>A seek made during the hero pick, waiting for the pick to end (ms; -1 none).</summary>
        public static float PendingMs { get; private set; } = -1f;
        /// <summary>The plugin has told the server the hero screen is gone.</summary>
        public static bool PastPickSent => _pastPickSent;

        /// <summary>Parse "speed=<n>;vt=<ms>;step=<k>;total=<n>;combat=<0|1>;shown=<ms>;rc=<0|1>;live=<0|1>;pend=<ms>;<text>",
        /// apply it, return the text. live=1 says the command this answers plays packets out live (a burst follows).</summary>
        private static string Apply(string reply)
        {
            string text = reply;
            float speed = float.NaN, vt = float.NaN;
            bool live = false, moved = false;
            int walk = 0;
            while (true)
            {
                int semi = text.IndexOf(';');
                int eq = text.IndexOf('=');
                if (semi < 0 || eq < 0 || eq > semi) break;
                string key = text.Substring(0, eq);
                string val = text.Substring(eq + 1, semi - eq - 1);
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) break;
                if (key == "speed") speed = f;
                else if (key == "vt") vt = f;
                else if (key == "step") Step = (int)f;
                else if (key == "total") Total = (int)f;
                else if (key == "combat") CombatPlaying = f != 0;
                else if (key == "shown") { ShownMs = f; ShownAt = Time.unscaledTime; }
                else if (key == "rc") Reconnecting = f != 0;
                else if (key == "live") live = f != 0;
                else if (key == "walk") walk = (int)f;
                else if (key == "blk") { } // the move jumps to a combat block
                else if (key == "fresh") { } // a fight held back is released (its first block arrives to be walked)
                else if (key == "mv") moved = f != 0;
                else if (key == "pend")
                {
                    bool was = PendingMs >= 0f;
                    PendingMs = f;
                    if (was != (f >= 0f)) ReplaySpeed.RefreshTimeScale(); // the pick's speed-up (ReplaySpeed.AnimationFactor)
                }
                else break; // not a key we know: the rest is the status text
                text = text.Substring(semi + 1);
            }
            if (!float.IsNaN(speed) && !float.IsNaN(vt)) ReplaySpeed.OnServerStatus(speed, vt);
            else if (!float.IsNaN(speed)) ReplaySpeed.Set(speed);
            if (moved) ReplaySpeed.OnMoveReply(walk);
            return text;
        }

        /// <summary>Send a command whose reply is not shown (status polls, the replay bar's moves and point requests).</summary>
        public static void SendSilent(string cmd) => Send(cmd);

        // The last moves sent (for the bug report), polls and loads left out.
        private const int KeptCommands = 10;
        private static readonly Queue<(float At, string Cmd)> Recent = new Queue<(float, string)>();

        /// <summary>The last moves sent, newest first, each with how long ago ("4 s ago: seek 120").</summary>
        public static string[] RecentCommands()
        {
            float now = Time.unscaledTime;
            return Recent.Reverse().Select(c => $"{Mathf.RoundToInt(now - c.At)} s ago: {c.Cmd}").ToArray();
        }

        private static void Send(string cmd)
        {
            if (!(cmd == "status" || cmd.StartsWith("points ") || cmd.StartsWith("load ") || cmd.StartsWith("blockat ")))
            {
                Recent.Enqueue((Time.unscaledTime, cmd));
                while (Recent.Count > KeptCommands) Recent.Dequeue();
            }
            try
            {
                if (_udp == null)
                {
                    _udp = new UdpClient(Plugin.LocalServerHost.Value, Plugin.ControlPort.Value);
                    _udp.BeginReceive(OnReceive, null);
                }
                byte[] data = Encoding.UTF8.GetBytes(cmd);
                _udp.Send(data, data.Length);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Replay control '{cmd}' failed: {e.Message}");
                Reset();
            }
        }

        // Runs on a thread-pool thread; hands the reply to Update() on the main thread.
        private static void OnReceive(IAsyncResult ar)
        {
            try
            {
                var udp = _udp;
                if (udp == null) return;
                System.Net.IPEndPoint from = null;
                byte[] data = udp.EndReceive(ar, ref from);
                Replies.Enqueue(Encoding.UTF8.GetString(data));
                udp.BeginReceive(OnReceive, null);
            }
            catch (ObjectDisposedException) { }
            catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset)
            {
                // ICMP port-unreachable: the server isn't running (yet: Watch polls it while it starts).
                Replies.Enqueue("Replay server not reachable");
                Plugin.Log.LogDebug("Replay control receive: " + e.Message);
                Reset();
            }
            catch (Exception e)
            {
                Replies.Enqueue("Replay server not reachable");
                Plugin.Log.LogWarning("Replay control receive: " + e.Message);
                Reset();
            }
        }

        private static void Reset()
        {
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        public static void Shutdown() => Reset();
    }
}
