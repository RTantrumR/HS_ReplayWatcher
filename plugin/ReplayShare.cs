using System;
using System.Diagnostics;
using System.IO;

namespace HsPuzzleBridge
{
    /// <summary>
    /// A row's share button: the game's shareable copy (hsserver.share: the account's and the machine's details
    /// blanked; the lobby's names stay, the leaderboard shows them) is written to %LOCALAPPDATA%\HS Replay
    /// Watcher\Shared (the copies before it removed) and put on the clipboard as a file, so Ctrl+V in a chat attaches
    /// it; the game's status line says so. The copy takes a second or two, in its own process.
    /// </summary>
    internal static class ReplayShare
    {
        private static Process _process;
        private static volatile bool _done;
        private static string _copy, _id;

        private static string Folder => Plugin.Shared;

        public static void Share(ReplayLibrary.View view, string id)
        {
            if (_process != null) return;
            string folder = ReplayLibrary.FolderOf(view);
            string game = folder != null ? ReplayLibrary.RecordingIn(folder, id) : null;
            if (game == null) { Status("Replays: this replay's file is missing", true); return; }
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (string old in Directory.GetFiles(Folder))
                    try { File.Delete(old); }
                    catch (Exception e) { Plugin.Log.LogDebug("Share: can't remove an old copy: " + e.Message); } // pasted somewhere still
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Share: can't make the shared folder: " + e.Message);
                Status("Replays: couldn't share the replay", true);
                return;
            }
            _id = id;
            _copy = Path.Combine(Folder, id + ".jsonl.gz");
            _done = false;
            var start = new ProcessStartInfo
            {
                FileName = Plugin.Python,
                Arguments = $"-m hsserver.share \"{game}\" \"{_copy}\"",
                WorkingDirectory = Plugin.Server,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            try
            {
                _process = new Process { StartInfo = start, EnableRaisingEvents = true };
                _process.Exited += (_, __) => _done = true;
                _process.Start();
                Status("Replays: preparing the replay to share…", false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Share: the copy didn't start ({start.FileName}): {e.Message}");
                _process = null;
                Status("Replays: couldn't share the replay", true);
            }
        }

        /// <summary>Per frame from Plugin.Update: the copy finished, its file goes on the clipboard (on this thread).</summary>
        public static void Update()
        {
            if (_process == null || !_done) return;
            int code = _process.ExitCode;
            string error = code != 0 ? _process.StandardError.ReadToEnd() : null;
            _process.Dispose();
            _process = null;
            if (code != 0 || !File.Exists(_copy))
            {
                Plugin.Log.LogWarning($"Share: the copy of {_id} failed: {error}");
                Status("Replays: couldn't share the replay", true);
                return;
            }
            if (!FileClipboard.SetFiles(new[] { _copy }))
            {
                Plugin.Log.LogWarning("Share: the clipboard is busy");
                Status("Replays: the clipboard is busy, try again", true);
                return;
            }
            Plugin.Log.LogInfo($"Share: {_id} on the clipboard ({new FileInfo(_copy).Length / 1024} KB)");
            Status("Replay copied: paste it into a chat with Ctrl+V", false);
        }

        private static void Status(string text, bool error)
        {
            try
            {
                if (error) UIStatus.Get()?.AddError(text);
                else UIStatus.Get()?.AddInfo(text);
            }
            catch (Exception e) { Plugin.Log.LogWarning("UIStatus: " + e.Message); }
        }
    }
}
