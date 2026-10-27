using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Blizzard.GameService.SDK.Client.Integration;
using PegasusShared;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The Replays page's data and flow: the recorded games (the catalog), and Watch.
    ///
    /// The rows come from recordings/.catalog.txt, written by the catalog scan (hsserver.catalog): opening the page
    /// shows what that file holds at once and runs a scan in the background, which adds new recordings, then
    /// re-reads it. No server runs for that. Watch starts the replay server (<see cref="ReplayServerProcess"/>),
    /// asks it to load the game ("load id", repeated until it answers "ready"; the server needs a moment to start),
    /// then starts a Battlegrounds find-game with a one-shot watch request, which the find-game intercept turns
    /// into the local spectator join (<see cref="LocalServer"/>). The lobby's own Play stays real matchmaking.
    /// </summary>
    internal static class ReplayLibrary
    {
        public sealed class Entry
        {
            public string Id, Start, Hero;
            public int Place, Turns, Minutes;
            public int? Rating, Change;
            public readonly List<Minion> Board = new List<Minion>(), Hand = new List<Minion>();
            public readonly List<string> HeroPowers = new List<string>(), Trinkets = new List<string>();
            /// <summary>The quest that traded the hero power away and its reward (the rows file's 13th field).</summary>
            public readonly List<string> HpQuest = new List<string>();
        }

        /// <summary>A card on the final board or in the final hand (a spell: 0 attack and health).</summary>
        public sealed class Minion
        {
            public string Card;
            public int Attack, Health;
            public bool Golden;
            public string Keywords = ""; // one letter each: T aunt, D ivine shield, W indfury, R eborn, S tealth, P oisonous, V enomous
        }

        private const float PollSeconds = 0.3f, LoadWaitSeconds = 30f;
        private const string RowsFile = ".catalog.txt";
        private const int BgSoloMission = 3459; // BaconDisplay.PlayButtonRelease's solo FindGame

        /// <summary>One list the page can show (the recent games, the saved ones, the imported ones: other players'
        /// and those converted from Power.log), each with its own rows file.</summary>
        public sealed class View
        {
            public readonly string Name;
            public readonly string Title; // the page's title while this view is shown
            public readonly string Label; // the view switch's text while this view is the next one
            public readonly List<Entry> Entries = new List<Entry>();
            public int Version; // bumped whenever Entries change (the page rebuilds that view's rows)
            public bool Loaded;
            public string RowsText;
            public View(string name, string title, string label) { Name = name; Title = title; Label = label; }
        }

        public static readonly View Recent = new View("recent", "Replays", "Recent replays"),
                                    Saved = new View("saved", "Saved replays", "Saved replays"),
                                    Imported = new View("imported", "Imported replays", "Imported replays");
        /// <summary>The page's views, in the switch's order (after the views: static fields start in order).</summary>
        public static readonly View[] Views = { Recent, Saved, Imported };
        /// <summary>The view on the page.</summary>
        public static View Current { get; private set; } = Recent;
        /// <summary>The view the switch goes to: the next one round the list.</summary>
        public static View Next => Views[(Array.IndexOf(Views, Current) + 1) % Views.Length];
        /// <summary>The view before the shown one, round the list.</summary>
        public static View Prev => Views[(Array.IndexOf(Views, Current) + Views.Length - 1) % Views.Length];
        public static List<Entry> Entries => Current.Entries;
        public static string WatchingId => _watchId;
        /// <summary>The game of the replay last joined (kept through the replay; the bug report names it).</summary>
        public static string PlayingId { get; private set; }
        public static string LastError { get; private set; }
        /// <summary>Ids of the games in the saved folder (its file names), and of the recent real games (the recent
        /// catalog's rows, seeds left out): the hearts and the unsave rules read them.</summary>
        public static readonly HashSet<string> SavedIds = new HashSet<string>(), RecentIds = new HashSet<string>();

        private static bool _open;
        private static string _watchId;
        private static float _watchStartedAt, _nextPoll;

        /// <summary>The page opened: the rows known so far, and a scan for new recordings.</summary>
        public static void Open()
        {
            _open = true;
            LastError = null;
            // The converter makes it too; here it exists for the scan and the server from the first open.
            try { Directory.CreateDirectory(ImportedDir); }
            catch (Exception e) { Plugin.Log.LogWarning("Replays: can't create the imported folder: " + e.Message); }
            SplitSavedDir();
            LoadRows();
            ReplayServerProcess.ScanCatalog();
        }

        /// <summary>Put a view on the page (every view's rows are kept built; the files are re-read in case
        /// something changed them meanwhile).</summary>
        public static void Show(View view)
        {
            if (view == Current) return;
            Current = view;
            LoadRows();
        }

        /// <summary>The folder the player chose, holding Recent, Saved and Imported.</summary>
        public static string SavedRoot => Plugin.SavedDir.Value;
        public static bool HasSavedDir => !string.IsNullOrEmpty(SavedRoot) && Directory.Exists(SavedRoot);
        /// <summary>The recent games (the recorder's folder, <see cref="Plugin.Recordings"/>, unless the config names one).</summary>
        public static string RecentDir => Path.Combine(SavedRoot, "Recent");
        /// <summary>The saved (starred) games.</summary>
        public static string SavedDir => Path.Combine(SavedRoot, "Saved");
        /// <summary>The imported games: the chosen folder's Imported, or, until one is chosen, the converter's folder.</summary>
        public static string ImportedDir => HasSavedDir ? Path.Combine(SavedRoot, "Imported") : Plugin.Logs;

        /// <summary>Our recordings' names (the recorder's yyyyMMdd_HHmmss_game..., the converter's ..._log...).</summary>
        private static readonly Regex RecordingName = new Regex(@"^\d{8}_\d{6}_.+\.jsonl(\.gz)?$");

        /// <summary>The chosen folder holds three: Recent, Saved and Imported. Games from before the split move in: those
        /// at its top into Saved, the converter's (Plugin.Logs) into Imported, the recorder's from before the folder
        /// was chosen (or while it was missing) into Recent; each folder's catalog files go with its games while the
        /// new folder has none (File.Move keeps the write times, so the scan's cache still knows them).
        /// Run at every page open, as the converter keeps writing to its own folder.</summary>
        public static void SplitSavedDir()
        {
            if (!HasSavedDir) return;
            MoveGames(SavedRoot, SavedDir);
            MoveGames(Plugin.Logs, ImportedDir);
            if (Plugin.RecordingsDir.Value.Length == 0) MoveGames(Plugin.DefaultRecordings, RecentDir);
        }

        private static void MoveGames(string from, string to)
        {
            try
            {
                Directory.CreateDirectory(to);
                if (!Directory.Exists(from)) return;
                bool catalog = !File.Exists(Path.Combine(to, RowsFile));
                int moved = 0;
                foreach (string path in Directory.GetFiles(from))
                {
                    string name = Path.GetFileName(path);
                    if (!RecordingName.IsMatch(name) && !(catalog && name.StartsWith(".catalog."))) continue;
                    string dst = Path.Combine(to, name);
                    if (File.Exists(dst)) continue;
                    // One file in use (a recording still open) stays for the next open; the others still move.
                    try { File.Move(path, dst); moved++; }
                    catch (IOException e) { Plugin.Log.LogWarning($"Replays: can't move {name} yet: {e.Message}"); }
                }
                if (moved > 0) Plugin.Log.LogInfo($"Replays: moved {moved} files from {from} to {to}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Replays: can't move the games from {from} to {to}: {e.Message}"); }
        }

        /// <summary>A game's recording in folder (compressed or not), or null.</summary>
        public static string RecordingIn(string folder, string id)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            foreach (string suffix in new[] { ".jsonl.gz", ".jsonl" })
            {
                string path = Path.Combine(folder, id + suffix);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Re-read which games are saved and which are recent (after a save, an unsave, a scan).</summary>
        public static void RefreshIds()
        {
            SavedIds.Clear();
            if (HasSavedDir)
            {
                try
                {
                    foreach (string path in Directory.GetFiles(SavedDir, "*.jsonl*"))
                    {
                        string name = Path.GetFileName(path);
                        if (name.EndsWith(".jsonl.gz")) SavedIds.Add(name.Substring(0, name.Length - 9));
                        else if (name.EndsWith(".jsonl")) SavedIds.Add(name.Substring(0, name.Length - 6));
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Replays: can't list the saved folder: " + e.Message); }
            }
            RecentIds.Clear();
            try
            {
                string rows = Path.Combine(Plugin.Recordings, RowsFile);
                if (File.Exists(rows))
                    foreach (string line in File.ReadAllLines(rows))
                    {
                        int bar = line.IndexOf('|');
                        if (bar > 0 && !line.Contains("_seed")) RecentIds.Add(line.Substring(0, bar));
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays: can't read the recent rows: " + e.Message); }
        }

        public static void Close() => _open = false;

        public static void Watch(string id)
        {
            if (_watchId != null) return;
            if (!BattleNet.IsConnected() || GameMgr.Get().IsFindingGame())
            {
                Fail("can't start a game right now");
                return;
            }
            ReplayServerProcess.EnsureRunning();
            _watchId = id;
            _watchStartedAt = Time.unscaledTime;
            _nextPoll = 0f;
            LastError = null;
        }

        /// <summary>Per frame from Plugin.Update.</summary>
        public static void Update()
        {
            float now = Time.unscaledTime;
            if (ReplayServerProcess.TakeScanDone() && _open) LoadRows();
            if (_watchId != null && now >= _nextPoll)
            {
                if (now - _watchStartedAt > LoadWaitSeconds)
                {
                    _watchId = null;
                    Fail("the replay took too long to load (see BepInEx/replay_server.log)");
                    ReplayServerProcess.Stop("the replay took too long to load");
                }
                else
                {
                    // Repeated until the answer is "ready": the first ones go out while the server is starting
                    // (no answer), and a load already under way or done is a no-op on the server.
                    _nextPoll = now + PollSeconds;
                    ReplayControl.SendSilent("load " + _watchId);
                }
            }
        }

        /// <summary>A view's folder (null: none yet).</summary>
        public static string FolderOf(View view) =>
            view == Saved ? (HasSavedDir ? SavedDir : null) : view == Imported ? ImportedDir : Plugin.Recordings;

        /// <summary>The rows file of a view's folder (null: none).</summary>
        public static string RowsPath(View view)
        {
            string folder = FolderOf(view);
            return folder != null ? Path.Combine(folder, RowsFile) : null;
        }

        /// <summary>A game saved or unsaved: its row goes into (or out of) the saved folder's rows file at once, so
        /// the Saved view is right the moment the star is clicked; the catalog scan (which decodes nothing for a
        /// copy it already knows) writes the same rows later.</summary>
        public static void SetSavedRow(string id, bool saved)
        {
            if (!saved) { SetRow(Saved, id, null); return; }
            string row = RowOf(Recent, id) ?? RowOf(Imported, id);
            if (row == null) { Plugin.Log.LogWarning("Replays: no row to copy for " + id); return; }
            SetRow(Saved, id, row);
        }

        /// <summary>A game's row in a view's rows file, or null.</summary>
        private static string RowOf(View view, string id)
        {
            foreach (string line in (view.RowsText ?? "").Split('\n'))
                if (line.StartsWith(id + "|")) return line.TrimEnd('\r');
            return null;
        }

        /// <summary>A game's row put into (row) or taken out of (null) a view's rows file, then the rows re-read.</summary>
        public static void SetRow(View view, string id, string row)
        {
            string path = RowsPath(view);
            if (path == null) return;
            try
            {
                var lines = new List<string>();
                if (File.Exists(path))
                    foreach (string line in File.ReadAllText(path).Split('\n'))
                        if (line.Trim().Length > 0 && !line.StartsWith(id + "|")) lines.Add(line.TrimEnd('\r'));
                if (row != null) lines.Add(row);
                lines.Sort((a, b) => string.CompareOrdinal(b, a)); // newest first, as the catalog writes them
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", lines.ToArray()));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Replays: can't update the {view.Name} rows: {e.Message}"); }
            LoadRows();
        }

        /// <summary>Every view's rows file: one game per line, newest first,
        /// id|start|hero|place|turns|minutes|rating|change|board|hero powers|trinkets|hand|hero power quest (board, hand: card:attack:health:golden[:keywords];
        /// lists comma-separated; see hsserver.catalog.row). Public: the saved view's file is also written by the page.</summary>
        public static void LoadRows()
        {
            RefreshIds();
            foreach (var view in Views) LoadRows(view);
        }

        private static void LoadRows(View view)
        {
            string path = RowsPath(view);
            string text;
            try { text = path != null && File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Replays: can't read {path}: {e.Message}");
                return;
            }
            // Unchanged rows (every open, and its scan, re-read the file) keep the page's built rows.
            if (view.Loaded && text == view.RowsText) return;
            view.RowsText = text;
            var entries = view.Entries;
            entries.Clear();
            foreach (string line in text.Split('\n'))
            {
                string[] f = line.TrimEnd('\r').Split('|');
                if (f.Length < 8) continue;
                var entry = new Entry
                {
                    Id = f[0], Start = f[1], Hero = f[2],
                    Place = ParseInt(f[3]) ?? 0, Turns = ParseInt(f[4]) ?? 0, Minutes = ParseInt(f[5]) ?? 0,
                    Rating = ParseInt(f[6]), Change = ParseInt(f[7]),
                };
                if (f.Length > 8) ParseCards(f[8], entry.Board);
                if (f.Length > 9) entry.HeroPowers.AddRange(f[9].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                if (f.Length > 10) entry.Trinkets.AddRange(f[10].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                if (f.Length > 11) ParseCards(f[11], entry.Hand);
                if (f.Length > 12) entry.HpQuest.AddRange(f[12].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                entries.Add(entry);
            }
            view.Loaded = true;
            view.Version++;
        }

        private static void ParseCards(string field, List<Minion> cards)
        {
            foreach (string m in field.Split(','))
            {
                string[] p = m.Split(':');
                if (p.Length < 4) continue;
                cards.Add(new Minion
                {
                    Card = p[0], Attack = ParseInt(p[1]) ?? 0, Health = ParseInt(p[2]) ?? 0, Golden = p[3] == "1",
                    Keywords = p.Length > 4 ? p[4] : "",
                });
            }
        }

        /// <summary>"library=&lt;idle|loading|ready|error&gt;;id=&lt;id&gt;;[error=&lt;text&gt;;]".</summary>
        public static void OnLibraryStatus(string reply)
        {
            if (_watchId == null) return;
            var head = Fields(reply, out _);
            head.TryGetValue("library", out string state);
            head.TryGetValue("id", out string id);
            if (id != _watchId) return;
            if (state == "error")
            {
                _watchId = null;
                head.TryGetValue("error", out string error);
                Fail("the replay failed to load: " + error);
                ReplayServerProcess.Stop("the replay failed to load");
            }
            else if (state == "ready")
            {
                Plugin.Log.LogInfo($"Replays: {id} is ready, joining");
                _watchId = null;
                PlayingId = id;
                LocalServer.RequestWatch();
                GameMgr.Get().FindGame(GameType.GT_BATTLEGROUNDS, FormatType.FT_WILD, BgSoloMission);
            }
        }

        private static void Fail(string message)
        {
            LastError = message;
            Plugin.Log.LogWarning("Replays: " + message);
            try { UIStatus.Get()?.AddError("Replays: " + message); } catch (Exception e) { Plugin.Log.LogWarning("UIStatus: " + e.Message); }
        }

        private static Dictionary<string, string> Fields(string reply, out string rest)
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
            d.TryGetValue(key, out string v) ? ParseInt(v) ?? 0 : 0;

        private static int? ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;
    }
}
