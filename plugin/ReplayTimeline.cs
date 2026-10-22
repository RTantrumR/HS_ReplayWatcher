using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The replay's timeline for <see cref="ReplayBar"/>: its events (the server's replay points), its length, its
    /// combats, where the replay is, and the moves the bar makes.
    ///
    /// Data: the server's "points" pages ("points=&lt;start&gt;;total=&lt;n&gt;;end=&lt;ms&gt;;combats=&lt;s&gt;-&lt;e&gt;,...;
    /// gaps=&lt;s&gt;-&lt;e&gt;,...;"
    /// then index|vt|turn|kind|verb|card|target|arg lines), fetched once per game and again if the point count
    /// changes. Card ids become names in the client's language through DefLoader.
    ///
    /// Position: the "shown" time of the server's replies, moved on locally between them at the replay speed
    /// (a combat too: the client animates it at that speed, and a pause freezes it).
    ///
    /// Reconnects: where the recorded client dropped and came back (a gap: nothing recorded), the server plays the
    /// game's reconnect popup sequence (<see cref="ReconnectPopup"/>) and resets to the reconnected state; it says so
    /// with "rc=1" in its replies, which the bar polls for quickly while the playhead nears a drop or after a move.
    ///
    /// Preview: on the Replays page (<see cref="Plugin.PanelToggleKey"/>), the bar shows a recorded game without a
    /// replay running. The server is started and loads the game; its points come from there; the bar's moves only
    /// move a simulated playhead. Leaving the page (or the key again) ends it and stops the server.
    /// </summary>
    internal static class ReplayTimeline
    {
        internal sealed class Point
        {
            public int Index, Turn;
            public float Ms;
            public float Back; // where a step back to this event lands (a little before it; the server's back_landing)
            public string Kind, Verb, Card, Target, Arg, Text;
        }

        private const float RequestRetrySeconds = 1f, PreviewLoadWaitSeconds = 30f, PreviewPollSeconds = 0.3f;
        // While a reconnect may be coming (the playhead this close before a drop, or this long after a move), the
        // server's state is polled this often, so its popup starts within a poll of the server's sequence.
        private const float NearGapMs = 4000f, AfterMoveSeconds = 2.5f, FastPollSeconds = 0.25f;
        private const float ReconnectAfterMs = 300f; // as the server's RECONNECT_AFTER_MS

        public static Point[] Points { get; private set; }
        public static float EndMs { get; private set; }
        /// <summary>Each combat's span on the timeline: its start to where the player was back in the shop.</summary>
        public static readonly List<Vector2> Combats = new List<Vector2>();
        /// <summary>Each drop: from the recorded client's last packet to where its reconnect landed.</summary>
        public static readonly List<Vector2> Gaps = new List<Vector2>();
        /// <summary>Bumped whenever the points, length or combats change (the bar rebuilds its marks).</summary>
        public static int Version { get; private set; }

        private static int _received;
        private static float _lastRequest = -10f;
        private static float _resumeSpeed = 1f;
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>();

        // ------------------------------------------------------------------ state

        /// <summary>A replay is on, or the lobby preview.</summary>
        public static bool Active => ReplaySpeed.InLocalGame || Previewing;

        public static bool Ready => Active && Points != null && _received >= Points.Length && Points.Length > 0;

        public static float Speed => Previewing ? _previewSpeed : ReplaySpeed.Speed;

        /// <summary>The speed a pause resumes at.</summary>
        public static float ResumeSpeed => Mathf.Max(1f, _resumeSpeed);

        public static bool CombatPlaying => !Previewing && ReplayControl.CombatPlaying;

        /// <summary>Where the replay is, in ms of the recording.</summary>
        public static float NowMs
        {
            get
            {
                if (Previewing) return _previewMs;
                if (ReplayControl.Reconnecting) return ReplayControl.ShownMs; // the server's clock stands still
                float elapsed = (Time.unscaledTime - ReplayControl.ShownAt) * 1000f;
                float ms = ReplayControl.ShownMs + elapsed * ReplaySpeed.Speed;
                if (ReplayControl.CombatPlaying)
                {
                    // Not past the span of the combat being played.
                    foreach (var c in Combats)
                        if (ReplayControl.ShownMs >= c.x && ReplayControl.ShownMs <= c.y) return Mathf.Min(ms, c.y);
                }
                return Mathf.Min(ms, EndMs > 0f ? EndMs : ms);
            }
        }

        /// <summary>Index of the last event reached (-1 before the first).</summary>
        public static int CurrentIndex
        {
            get
            {
                if (Points == null) return -1;
                if (!Previewing) return Math.Min(Points.Length - 1, ReplayControl.Step - 1);
                return LastAtOrBefore(_previewMs);
            }
        }

        public static int LastAtOrBefore(float ms)
        {
            int lo = 0, hi = Points.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Points[mid] != null && Points[mid].Ms <= ms + 1f) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>The drop whose gap holds ms, or null.</summary>
        public static Vector2? GapAt(float ms)
        {
            foreach (var g in Gaps)
                if (ms >= g.x && ms < g.y) return g;
            return null;
        }

        /// <summary>The combat whose span holds ms, or null.</summary>
        public static Vector2? CombatAt(float ms)
        {
            foreach (var c in Combats)
                if (ms >= c.x && ms < c.y) return c;
            return null;
        }

        public static int TurnAt(float ms)
        {
            int i = LastAtOrBefore(ms);
            return i >= 0 ? Points[i].Turn : Points.Length > 0 ? Points[0].Turn : 0;
        }

        // ------------------------------------------------------------------ moves

        public static void PreviousEvent()
        {
            if (Previewing) { PreviewBack(); return; }
            Moved();
            ReplayControl.SendSilent("step -1");
        }

        public static void NextEvent()
        {
            if (Previewing) { PreviewSeekPoint(LastAtOrBefore(_previewMs) + 1); return; }
            Moved(1);
            ReplayControl.SendSilent("step 1");
        }

        public static void SeekPoint(int index)
        {
            if (Previewing) { PreviewSeekPoint(index); return; }
            Moved();
            ReplayControl.SendSilent("seek " + index);
        }

        /// <summary>To a moment; direction (-1/0/1) decides which edge of a combat a moment inside it lands on.</summary>
        public static void SeekTime(float ms, int direction)
        {
            if (Previewing) { PreviewSeekTime(ms, direction); return; }
            Moved();
            ReplayControl.SendSilent($"time {Mathf.RoundToInt(ms).ToString(CultureInfo.InvariantCulture)} {direction}");
        }

        /// <summary>The first event of the next turn.</summary>
        public static void NextTurn()
        {
            if (Points == null || Points.Length == 0) return;
            int turn = TurnAt(NowMs);
            for (int i = Math.Max(0, LastAtOrBefore(NowMs)); i < Points.Length; i++)
                if (Points[i] != null && Points[i].Turn > turn) { SeekPoint(i); return; }
            SeekPoint(Points.Length - 1);
        }

        /// <summary>The first event of this turn, or of the turn before when this turn has only just begun.</summary>
        public static void PreviousTurn()
        {
            if (Points == null || Points.Length == 0) return;
            int turn = TurnAt(NowMs);
            int first = FirstOfTurn(turn);
            if (first < 0 || NowMs - Points[first].Ms < TurnRestartMs) first = FirstOfTurn(turn - 1);
            if (first < 0) first = 0;
            SeekPoint(first);
        }

        private const float TurnRestartMs = 1500f; // this far into a turn, "previous turn" restarts it

        private static int FirstOfTurn(int turn)
        {
            for (int i = 0; i < Points.Length; i++)
                if (Points[i] != null && Points[i].Turn >= turn) return Points[i].Turn == turn ? i : -1;
            return -1;
        }

        public static void TogglePause()
        {
            float speed = Speed;
            if (speed > 0f) _resumeSpeed = speed;
            SetSpeed(speed > 0f ? 0f : Mathf.Max(1f, _resumeSpeed));
        }

        /// <summary>Next speed x1 → x2 → x3 → x4 → x1; while paused, the speed play will resume at.</summary>
        public static void CycleSpeed()
        {
            float speed = Speed;
            if (speed <= 0f) _resumeSpeed = ResumeSpeed >= 4f ? 1f : ResumeSpeed + 1f;
            else SetSpeed(speed >= 4f ? 1f : speed + 1f);
        }

        private static void SetSpeed(float speed)
        {
            if (Previewing) { _previewSpeed = speed; return; }
            ReplayControl.SendSilent("speed " + Mathf.RoundToInt(speed).ToString(CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ data

        public static void OnPointsPage(string reply)
        {
            try
            {
                var head = Fields(reply, out string rest);
                int start = Int(head, "points"), total = Int(head, "total");
                if (total <= 0) return;
                if (Points == null || Points.Length != total)
                {
                    Points = new Point[total];
                    _received = 0;
                }
                if (head.TryGetValue("end", out string end))
                    EndMs = float.Parse(end, CultureInfo.InvariantCulture);
                if (head.TryGetValue("gaps", out string gaps))
                {
                    Gaps.Clear();
                    foreach (string span in gaps.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] se = span.Split('-');
                        if (se.Length == 2)
                            Gaps.Add(new Vector2(float.Parse(se[0], CultureInfo.InvariantCulture),
                                                 float.Parse(se[1], CultureInfo.InvariantCulture)));
                    }
                }
                if (head.TryGetValue("combats", out string combats))
                {
                    Combats.Clear();
                    foreach (string span in combats.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] se = span.Split('-');
                        if (se.Length == 2)
                            Combats.Add(new Vector2(float.Parse(se[0], CultureInfo.InvariantCulture),
                                                    float.Parse(se[1], CultureInfo.InvariantCulture)));
                    }
                }
                int next = start;
                foreach (string line in rest.Split('\n'))
                {
                    string[] f = line.Split('|');
                    if (f.Length < 8) continue;
                    int i = int.Parse(f[0], CultureInfo.InvariantCulture);
                    if (i < 0 || i >= total) continue;
                    if (Points[i] == null) _received++;
                    Points[i] = new Point
                    {
                        Index = i, Ms = float.Parse(f[1], CultureInfo.InvariantCulture),
                        Turn = int.Parse(f[2], CultureInfo.InvariantCulture),
                        Kind = f[3], Verb = f[4], Card = f[5], Target = f[6], Arg = f[7],
                    };
                    Points[i].Back = f.Length > 8 ? float.Parse(f[8], CultureInfo.InvariantCulture) : Points[i].Ms;
                    next = i + 1;
                }
                Version++;
                if (_received < total && next < total) Request(next);
                else if (_received >= total)
                {
                    Plugin.Log.LogInfo($"Replay timeline: {total} events, {Combats.Count} combats, {Gaps.Count} reconnects, {EndMs / 60000f:0.0} min");
                    if (Previewing && _previewMs <= 0f) _previewMs = Points[0].Ms;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Replay timeline: bad points page: " + e.Message);
            }
        }

        private static void Request(int start)
        {
            _lastRequest = Time.unscaledTime;
            ReplayControl.SendSilent("points " + start);
        }

        private static void Clear()
        {
            Points = null;
            _received = 0;
            EndMs = 0f;
            Combats.Clear();
            Gaps.Clear();
            Version++;
        }

        /// <summary>Per frame from Plugin.Update: fetching the points, the preview.</summary>
        public static void Update()
        {
            if (ReplaysUi.PageShown && !ReplaySpeed.InLocalGame && Input.GetKeyDown(Plugin.PanelToggleKey.Value))
                TogglePreview(ReplayLibrary.Entries.Count > 0 ? ReplayLibrary.Entries[0].Id : null);
            UpdatePreview();
            if (!Active)
            {
                if (Points != null) Clear();
                if (ReconnectPopup.Playing) ReconnectPopup.Stop();
                return;
            }
            WatchReconnects();
            int total = Previewing ? (Points?.Length ?? 0) : ReplayControl.Total;
            if (Time.unscaledTime - _lastRequest < RequestRetrySeconds) return;
            if (Previewing && !_previewLoaded) return;
            if (Points == null || (total > 0 && Points.Length != total)) Request(0);
            else if (_received < Points.Length) Request(Array.FindIndex(Points, p => p == null));
        }

        private static float _lastMoveAt = -100f, _nextFastPoll;
        private static bool _wasReconnecting;

        private static void Moved(int walk = 0)
        {
            _lastMoveAt = Time.unscaledTime;
            ReplaySpeed.NoteMove(walk);
        }

        /// <summary>A real replay: poll the server quickly while a reconnect may be coming, and play the popup when the
        /// server starts one.</summary>
        private static void WatchReconnects()
        {
            if (Previewing) return;
            bool reconnecting = ReplayControl.Reconnecting;
            if (reconnecting && !_wasReconnecting)
            {
                Plugin.Log.LogInfo($"Replay: the server plays a reconnect at {Clock(ReplayControl.ShownMs)}");
                ReconnectPopup.Play();
            }
            _wasReconnecting = reconnecting;
            if (Gaps.Count == 0) return;
            float now = Time.unscaledTime, ms = NowMs;
            bool near = reconnecting || now - _lastMoveAt < AfterMoveSeconds;
            foreach (var g in Gaps)
                if (ms >= g.x - NearGapMs && ms <= g.y) near = true;
            if (!near || now < _nextFastPoll) return;
            _nextFastPoll = now + FastPollSeconds;
            ReplayControl.SendSilent("status");
        }

        // ------------------------------------------------------------------ labels

        public static string Name(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return "";
            if (Names.TryGetValue(cardId, out string name)) return name;
            try { name = DefLoader.Get()?.GetEntityDef(cardId)?.GetName(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Replay timeline: no name for {cardId}: {e.Message}"); }
            if (string.IsNullOrEmpty(name)) name = cardId;
            Names[cardId] = name;
            return name;
        }

        public static string Describe(Point p)
        {
            if (p.Text != null) return p.Text;
            string card = Name(p.Card), target = Name(p.Target);
            string on = target.Length > 0 ? " on " + target : "";
            switch (p.Verb)
            {
                case "hero": p.Text = "Hero: " + card; break;
                case "pick": p.Text = "Pick " + card; break;
                case "buy": p.Text = "Buy " + card; break;
                case "sell": p.Text = "Sell " + card; break;
                case "play": p.Text = "Play " + card + on; break;
                case "cast": p.Text = "Cast " + card + on; break;
                case "upgrade": p.Text = p.Arg.Length > 0 ? "Upgrade tavern to tier " + p.Arg : "Upgrade tavern"; break;
                case "refresh": p.Text = "Refresh"; break;
                case "freeze": p.Text = "Freeze"; break;
                case "hero power": p.Text = "Hero Power: " + card + on; break;
                case "move": p.Text = "Move " + card; break;
                case "move in shop": p.Text = "Move " + card + " in shop"; break;
                case "activate": p.Text = "Activate " + card + on; break;
                case "combat": p.Text = card.Length > 0 ? "Combat vs " + card : "Combat"; break;
                case "attack": p.Text = card + " attacks" + (target.Length > 0 ? " " + target : ""); break;
                case "deaths": p.Text = "Deaths"; break;
                case "trigger": p.Text = card.Length > 0 ? card + " triggers" : "Trigger"; break;
                case "end turn": p.Text = "End turn"; break;
                default: p.Text = card.Length > 0 ? card + on : "Action"; break;
            }
            if (p.Kind == "ce") p.Text = "Back in the tavern";
            if (p.Kind == "rc") p.Text = "Reconnected";
            return p.Text;
        }

        public static string Clock(float ms)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(ms / 1000f));
            return $"{s / 60}:{s % 60:00}";
        }

        // ------------------------------------------------------------------ preview

        public static bool Previewing { get; private set; }
        public static string PreviewId => _previewId;
        private static string _previewId;
        private static bool _previewLoaded;
        private static float _previewMs, _previewSpeed = 1f, _previewStartedAt, _nextPreviewPoll;

        /// <summary>Show or hide the bar for game id on the Replays page.</summary>
        public static void TogglePreview(string id)
        {
            if (Previewing) { StopPreview("preview closed"); return; }
            if (id == null) return;
            Previewing = true;
            _previewId = id;
            _previewLoaded = false;
            _previewMs = 0f;
            _previewSpeed = 1f;
            _previewStartedAt = Time.unscaledTime;
            _nextPreviewPoll = 0f;
            Clear();
            ReplayServerProcess.EnsureRunning();
            Plugin.Log.LogInfo("Replay bar preview: " + id);
        }

        public static void StopPreview(string why)
        {
            if (!Previewing) return;
            Previewing = false;
            _previewId = null;
            Clear();
            if (!LocalServer.Active) ReplayServerProcess.Stop(why);
            Plugin.Log.LogInfo("Replay bar preview off (" + why + ")");
        }

        /// <summary>"library=..." replies while the preview loads its game.</summary>
        public static void OnLibraryStatus(string reply)
        {
            if (!Previewing || _previewLoaded) return;
            var head = Fields(reply, out _);
            head.TryGetValue("library", out string state);
            head.TryGetValue("id", out string id);
            if (id != _previewId) return;
            if (state == "ready")
            {
                _previewLoaded = true;
                Request(0);
            }
            else if (state == "error") StopPreview("the game failed to load");
        }

        private static void UpdatePreview()
        {
            if (!Previewing) return;
            if (!ReplaysUi.PageShown || LocalServer.Active) { StopPreview("left the Replays page"); return; }
            float now = Time.unscaledTime;
            if (!_previewLoaded)
            {
                if (now - _previewStartedAt > PreviewLoadWaitSeconds) { StopPreview("the game took too long to load"); return; }
                if (now < _nextPreviewPoll) return;
                _nextPreviewPoll = now + PreviewPollSeconds;
                ReplayControl.SendSilent("load " + _previewId);
                return;
            }
            if (!Ready) return;
            if (_previewGap.HasValue)
            {
                // The sequence: held at the drop for the first half, at the reconnect for the second.
                if (ReconnectPopup.Progress >= 0.5f && _previewMs < _previewGap.Value.y)
                {
                    _previewMs = _previewGap.Value.y;
                    Plugin.Log.LogInfo($"Replay bar preview: jumped to the reconnect at {Clock(_previewMs)}");
                }
                if (!ReconnectPopup.Playing) _previewGap = null;
                return;
            }
            float was = _previewMs;
            _previewMs = Mathf.Min(EndMs, _previewMs + Time.unscaledDeltaTime * 1000f * _previewSpeed);
            foreach (var g in Gaps)
                if (was < g.x + ReconnectAfterMs && _previewMs >= g.x + ReconnectAfterMs)
                {
                    _previewMs = g.x + ReconnectAfterMs;
                    _previewGap = g;
                    Plugin.Log.LogInfo($"Replay bar preview: reached the drop at {Clock(g.x)}, reconnect lands at {Clock(g.y)}");
                    ReconnectPopup.Play();
                    break;
                }
        }

        private static Vector2? _previewGap;

        private static void PreviewSeekPoint(int index)
        {
            if (!Ready) return;
            _previewBackAnchor = -1;
            index = Mathf.Clamp(index, 0, Points.Length - 1);
            _previewMs = Points[index].Ms;
        }

        private static int _previewBackAnchor = -1;

        /// <summary>As the server's _step_back: the last event reached, or right after a step back the one before
        /// it, landing a little before it (Point.Back).</summary>
        private static void PreviewBack()
        {
            if (!Ready) return;
            int a = _previewBackAnchor;
            int target = a >= 0 && _previewMs <= Points[a].Ms + 1f ? a - 1 : LastAtOrBefore(_previewMs);
            target = Mathf.Clamp(target, 0, Points.Length - 1);
            _previewMs = Points[target].Back;
            _previewBackAnchor = target;
        }

        /// <summary>As the server's ReplayData.time_target.</summary>
        private static void PreviewSeekTime(float ms, int direction)
        {
            if (!Ready) return;
            _previewBackAnchor = -1;
            ms = Mathf.Clamp(ms, Points[0].Ms, EndMs);
            foreach (var g in Gaps)
                if (ms > g.x && ms < g.y) { _previewMs = g.y; return; }
            foreach (var c in Combats)
                if (ms >= c.x && ms < c.y) { ms = direction > 0 ? c.y : c.x; break; }
            _previewMs = ms;
        }

        // ------------------------------------------------------------------ parsing

        internal static Dictionary<string, string> Fields(string reply, out string rest)
        {
            var d = new Dictionary<string, string>();
            rest = reply;
            while (true)
            {
                int semi = rest.IndexOf(';'), eq = rest.IndexOf('=');
                if (semi < 0 || eq < 0 || eq > semi) break;
                d[rest.Substring(0, eq)] = rest.Substring(eq + 1, semi - eq - 1);
                rest = rest.Substring(semi + 1);
            }
            return d;
        }

        private static int Int(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out string v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}
