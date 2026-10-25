using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    [BepInPlugin("com.testhsserver.puzzlebridge", "HS Replay Watcher", "0.28.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> RecorderEnabled, RecordLocalSessions;
        internal static ConfigEntry<string> RecordingsDir;
        internal static ConfigEntry<string> LocalServerHost;
        internal static ConfigEntry<int> LocalServerPort;
        internal static ConfigEntry<int> LocalGameHandle;
        internal static ConfigEntry<int> ControlPort, KeepGames;
        internal static ConfigEntry<KeyCode> PanelToggleKey;
        internal static ConfigEntry<float> EditorX, EditorY, BarX, BarY, ActionBoost, ActionBoostSeconds;
        internal static ConfigEntry<bool> BarCompact, FrameProbe, NoAskRemove, NoAskDelete;
        internal static ConfigEntry<KeyCode> UiDumpKey, EditorKey;
        internal static ConfigEntry<string> PythonExe, ServerDir, RowLayout, SavedDir, IssuesUrl, SortSaved, SortImported;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            RecorderEnabled = Config.Bind("Recorder", "Enabled", true,
                "Write every game-server packet (both directions) to a JSONL file per game connection.");
            RecordingsDir = Config.Bind("Recorder", "Directory", "",
                "Folder that receives the recordings. Empty: %LOCALAPPDATA%\\HS Replay Watcher\\Recordings.");
            RecordLocalSessions = Config.Bind("Recorder", "RecordLocalSessions", false,
                "Also record connections to the local replay server (watching a replay); a dev aid.");
            LocalServerHost = Config.Bind("LocalServer", "Host", "127.0.0.1", "Local game server address.");
            LocalServerPort = Config.Bind("LocalServer", "Port", 3724, "Local game server port.");
            LocalGameHandle = Config.Bind("LocalServer", "GameHandle", 1, "Game handle sent in the handshake (must be non-zero).");
            ControlPort = Config.Bind("LocalServer", "ControlPort", 3725,
                "UDP port of the replay server's control channel (+ / - speed, Enter = next recorded action).");
            PanelToggleKey = Config.Bind("Panel", "ToggleKey", KeyCode.F10,
                "Key that hides/shows the replay bar in a replay; on the Replays page it previews the bar for the newest game.");
            PythonExe = Config.Bind("Library", "PythonExe", "",
                "Python that runs the replay server for the Replays page. Empty: the installed one (BepInEx\\HSReplayWatcher\\python).");
            ServerDir = Config.Bind("Library", "ServerDir", "",
                "The replay server's folder (tools/replay_server.py is run from here). Empty: the installed one (BepInEx\\HSReplayWatcher\\server).");
            KeepGames = Config.Bind("Library", "KeepGames", 50, new ConfigDescription(
                "How many of the newest games keep their recordings; older ones are deleted at the next catalog scan.",
                new AcceptableValueRange<int>(5, 50)));
            SavedDir = Config.Bind("Library", "SavedDir", "",
                "Folder for the saved and imported games (its Saved and Imported folders), outside the Hearthstone folder " +
                "so updates and reinstalls never touch it. Empty: asked for (a folder window) the first time a game is " +
                "saved or imported.");
            NoAskRemove = Config.Bind("Library", "NoAskRemove", false,
                "Remove saved or imported replays without asking (the Replays page's settings).");
            NoAskDelete = Config.Bind("Library", "NoAskDelete", false,
                "With NoAskRemove: also delete without asking, when unstarring a saved replay deletes it for good.");
            SortSaved = Config.Bind("Library", "SortSaved", "Newest first",
                "The saved replays' order (the page's sort button): Newest first, Oldest first, Best place, By hero, Longest.");
            SortImported = Config.Bind("Library", "SortImported", "Newest first",
                "The imported replays' order (the page's sort button): Newest first, Oldest first, Best place, By hero, Longest.");
            RowLayout = Config.Bind("Library", "RowLayout", "",
                "Replays page row layout overrides, e.g. \"Pitch=15;HeroScale=3.4\" (see ReplaysUi.Layout; Alt+F12 reloads).");
            EditorKey = Config.Bind("Library", "EditorKey", KeyCode.F11,
                "Opens the Replays page's row layout editor (while the page is open).");
            EditorX = Config.Bind("Library", "EditorX", 20f, "Where the layout editor was dragged to (1080p units).");
            EditorY = Config.Bind("Library", "EditorY", 70f, "Where the layout editor was dragged to (1080p units).");
            BarX = Config.Bind("Panel", "BarX", -1f,
                "Where the replay bar was dragged to: its left edge, 1080p units from the screen's left (-1 = centred).");
            BarY = Config.Bind("Panel", "BarY", -1f,
                "Where the replay bar was dragged to: its top edge, 1080p units from the screen's top (-1 = at the top).");
            BarCompact = Config.Bind("Panel", "BarCompact", false, "The replay bar shows only its buttons (no timeline).");
            ActionBoost = Config.Bind("Replay", "ActionBoost", 4f,
                "How much faster the watched player's own plays, buys and sells move their card in a replay (1 = off): " +
                "the player's client never showed that travel (the card was already where they dropped it).");
            ActionBoostSeconds = Config.Bind("Replay", "ActionBoostSeconds", 0.5f,
                "How much of such an action (game seconds, from its start) is sped up: the card's travel.");
            IssuesUrl = Config.Bind("Report", "IssuesUrl", "https://github.com/RTantrumR/HS_ReplayWatcher/issues/new",
                "The bug button opens a new issue here (its form, .github/ISSUE_TEMPLATE/bug_report.yml, takes the details).");
            UiDumpKey = Config.Bind("Debug", "UiDumpKey", KeyCode.F12,
                "With Ctrl: write the scene's UI tree to BepInEx/ui_dump_N.txt (dev tool).");
            FrameProbe = Config.Bind("Debug", "FrameProbe", false,
                "Log every frame over 40 ms during a replay or on the Replays page, with the time each part of the plugin took (dev tool).");

            new Harmony("com.testhsserver.puzzlebridge").PatchAll();
            Log.LogInfo($"HS Replay Watcher loaded (recorder {(RecorderEnabled.Value ? "on" : "off")}, dir {Recordings})");
        }

        /// <summary>What the installer adds beside BepInEx: the replay server and its Python.</summary>
        private static string InstallDir => Path.Combine(Paths.BepInExRootPath, "HSReplayWatcher");

        private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HS Replay Watcher");

        /// <summary>The recordings folder: the config's, else one under %LOCALAPPDATA% (an install keeps none in HS).</summary>
        internal static string Recordings => RecordingsDir.Value.Length > 0 ? RecordingsDir.Value : Path.Combine(DataDir, "Recordings");

        /// <summary>The converter's default output (python -m hsserver.powerlog): the imported games until a saved folder
        /// is chosen, whose Imported folder then takes them (<see cref="ReplayLibrary.SplitSavedDir"/>).</summary>
        internal static string Logs => Path.Combine(DataDir, "Logs");

        /// <summary>The bug reports' folders (<see cref="BugReport"/>).</summary>
        internal static string Reports => Path.Combine(DataDir, "Reports");

        /// <summary>The shareable copy last put on the clipboard (<see cref="ReplayShare"/>).</summary>
        internal static string Shared => Path.Combine(DataDir, "Shared");

        internal static string Server =>ServerDir.Value.Length > 0 ? ServerDir.Value : Path.Combine(InstallDir, "server");

        internal static string Python => PythonExe.Value.Length > 0 ? PythonExe.Value : Path.Combine(InstallDir, "python", "python.exe");

        private void Update()
        {
            if (!FrameProbe.Value)
            {
                LocalServer.Update();
                ReplayControl.Update();
                ReplayLibrary.Update();
                ReplaysUi.Update();
                ReplaysEditor.Update();
                ReplaySpeed.Update();
                SeekDrain.Update();
                ReplayTimeline.Update();
                ReplayBar.Update();
                ReconnectPopup.Update();
                FolderPicker.Update();
                ReplayShare.Update();
                ReplaysUi.UpdateImport();
                SpectatorJoin.Update();
                CombatDoneNote.Update();
                UiDump.Update();
                RopeOnReplayClock.DriveRope();
                ReplayTelemetry.Update();
                return;
            }
            // Frame probe: how long each part takes, logged for slow frames where the plugin is at work.
            _probe.Length = 0;
            Probe("server", LocalServer.Update);
            Probe("control", ReplayControl.Update);
            Probe("library", ReplayLibrary.Update);
            Probe("page", ReplaysUi.Update);
            Probe("editor", ReplaysEditor.Update);
            Probe("speed", ReplaySpeed.Update);
            Probe("drain", SeekDrain.Update);
            Probe("timeline", ReplayTimeline.Update);
            Probe("bar", ReplayBar.Update);
            Probe("reconnect", ReconnectPopup.Update);
            Probe("picker", FolderPicker.Update);
            Probe("share", ReplayShare.Update);
            Probe("import", ReplaysUi.UpdateImport);
            Probe("spectator", SpectatorJoin.Update);
            Probe("combatnote", CombatDoneNote.Update);
            Probe("dump", UiDump.Update);
            Probe("rope", RopeOnReplayClock.DriveRope);
            Probe("telemetry", ReplayTelemetry.Update);
            float frameMs = Time.unscaledDeltaTime * 1000f;
            bool watched = ReplaySpeed.InLocalGame || ReplaysUi.PageShown;
            if (frameMs > 40f && watched)
                Log.LogInfo($"Frame {frameMs:0} ms (plugin {_probeTotalMs:0.0} ms:{_probe})");
            // Once a second: how the frames went (count, mean, worst, over 40 ms) and the page's active rows.
            if (watched)
            {
                _secFrames++;
                _secMs += frameMs;
                _secMax = Mathf.Max(_secMax, frameMs);
                if (frameMs > 40f) _secSlow++;
            }
            if (Time.unscaledTime - _secStart >= 1f)
            {
                if (_secFrames > 0)
                    Log.LogInfo($"Second: {_secFrames} frames, mean {_secMs / _secFrames:0} ms, worst {_secMax:0} ms, " +
                                $"{_secSlow} over 40 ms; {ReplaysUi.ProbeRows}");
                _secStart = Time.unscaledTime;
                _secFrames = _secSlow = 0;
                _secMs = _secMax = 0f;
            }
        }

        private static float _secStart, _secMs, _secMax;
        private static int _secFrames, _secSlow;

        private static readonly System.Text.StringBuilder _probe = new System.Text.StringBuilder();
        private static readonly System.Diagnostics.Stopwatch _probeWatch = new System.Diagnostics.Stopwatch();
        private static double _probeTotalMs;

        private static void Probe(string name, System.Action part)
        {
            if (_probe.Length == 0) _probeTotalMs = 0;
            _probeWatch.Restart();
            part();
            double ms = _probeWatch.Elapsed.TotalMilliseconds;
            _probeTotalMs += ms;
            if (ms >= 0.5) _probe.Append(' ').Append(name).Append(' ').Append(ms.ToString("0.0"));
        }

        private void LateUpdate()
        {
            if (!FrameProbe.Value) { PageSlideGuard.LateUpdate(); return; }
            _probeWatch.Restart();
            PageSlideGuard.LateUpdate();
            double ms = _probeWatch.Elapsed.TotalMilliseconds;
            if (ms >= 0.5) Log.LogInfo($"Slide guard {ms:0.0} ms");
        }

        private void OnGUI()
        {
            ReplaysEditor.OnGUI();
        }

        private void OnApplicationQuit() => ReplayServerProcess.Stop("HS is quitting");

        private void OnDestroy()
        {
            ReplayControl.Shutdown();
            PacketRecorder.Close();
        }
    }
}
