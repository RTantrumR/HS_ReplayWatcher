using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The bug button's report: a GitHub issue opened in the browser with the details pre-filled, and the files to
    /// attach made ready. No server of ours: the issue form (.github/ISSUE_TEMPLATE/bug_report.yml) takes its
    /// "details" field from the URL, and the player attaches the rest.
    ///
    /// The click takes a screenshot first (the next frame's end, before anything opens), then asks (an alert popup)
    /// whether to open GitHub. On yes:
    /// - the screenshot goes on the clipboard as an image, so Ctrl+V in the issue adds it;
    /// - a report folder (%LOCALAPPDATA%\HS Replay Watcher\Reports\&lt;time&gt;) gets the screenshot (PNG), the tails of
    ///   BepInEx's log and the replay server's log, and in a replay a shareable copy of the game
    ///   (hsserver.share: the account's and the machine's details blanked); Explorer opens on it;
    /// - the issue page opens: versions, PC, where in the replay, the last moves and the last warnings.
    /// Paths in the texts have the Windows user name replaced. The newest <see cref="KeepReports"/> folders are kept.
    /// </summary>
    internal static class BugReport
    {
        private const int KeepReports = 10, LogTailLines = 500, MaxUrlLength = 7500;

        private static bool _busy;

        /// <summary>Start a report from the bar or the Replays page.</summary>
        public static void Start()
        {
            if (_busy) return;
            _busy = true;
            Plugin.Instance.StartCoroutine(Capture());
        }

        private static IEnumerator Capture()
        {
            // A frame for the button's tooltip to go, then the frame as drawn.
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D shot = null;
            try
            {
                // The client's Unity is stripped of ScreenCapture's texture capture: the frame is read back here.
                shot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                shot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                shot.Apply(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bug report: no screenshot: " + e.Message); }
            string details = Details();
            string game = GameRecording();
            var info = new AlertPopup.PopupInfo
            {
                m_headerText = "Report a bug",
                m_text = "This opens a new issue on GitHub with your versions and PC details filled in.\n\n" +
                         "The screenshot is copied: press Ctrl+V in the issue to add it. Drag the files from the folder " +
                         "that opens" + (game != null ? " (the game and the logs)" : " (the logs)") +
                         " into the issue too, and say what went wrong.",
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_iconSet = AlertPopup.PopupInfo.IconSet.Default,
                m_confirmText = "Open GitHub",
                m_cancelText = GameStrings.Get("GLOBAL_CANCEL"),
                m_keyboardEscIsCancel = true,
                m_responseCallback = (response, _) =>
                {
                    try
                    {
                        if (response == AlertPopup.Response.CONFIRM) Send(shot, details, game);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Bug report: " + e); }
                    finally
                    {
                        if (shot != null) UnityEngine.Object.Destroy(shot);
                        _busy = false;
                    }
                },
            };
            try { DialogManager.Get().ShowPopup(info); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Bug report popup: " + e);
                if (shot != null) UnityEngine.Object.Destroy(shot);
                _busy = false;
            }
        }

        private static void Send(Texture2D shot, string details, string game)
        {
            string folder = Path.Combine(Plugin.Reports, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(folder);
            if (shot != null)
            {
                File.WriteAllBytes(Path.Combine(folder, "screenshot.png"), shot.EncodeToPNG()); // the client has no JPG encoder
                if (!Clipboard.SetImage(shot)) Plugin.Log.LogWarning("Bug report: the clipboard is busy; the screenshot is only in the folder");
            }
            WriteTail(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), Path.Combine(folder, "LogOutput.log"));
            WriteTail(Path.Combine(Paths.BepInExRootPath, "replay_server.log"), Path.Combine(folder, "replay_server.log"));
            Prune();
            Application.OpenURL(IssueUrl(details));
            if (game == null) { OpenFolder(folder); return; }
            // The shareable copy takes a second or two (the handshake is re-encoded): the folder opens once it's there.
            string copy = Path.Combine(folder, Path.GetFileName(game).Replace(".jsonl.gz", "").Replace(".jsonl", "") + ".jsonl.gz");
            var start = new ProcessStartInfo
            {
                FileName = Plugin.Python,
                Arguments = $"-m hsserver.share \"{game}\" \"{copy}\"",
                WorkingDirectory = Plugin.Server,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            try
            {
                var p = new Process { StartInfo = start, EnableRaisingEvents = true };
                p.Exited += (_, __) =>
                {
                    if (p.ExitCode != 0) Plugin.Log.LogWarning("Bug report: the game copy failed: " + p.StandardError.ReadToEnd());
                    OpenFolder(folder);
                    p.Dispose();
                };
                p.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Bug report: the game copy didn't start ({start.FileName}): {e.Message}");
                OpenFolder(folder);
            }
        }

        private static void OpenFolder(string folder)
        {
            try { Process.Start("explorer.exe", $"\"{folder}\"")?.Dispose(); }
            catch (Exception e) { Plugin.Log.LogWarning("Bug report: can't open the folder: " + e.Message); }
        }

        /// <summary>The issue form's page with its title and details pre-filled (the details cut to fit a URL).</summary>
        private static string IssueUrl(string details)
        {
            string head = Plugin.IssuesUrl.Value + "?template=bug_report.yml&title=" + Uri.EscapeDataString("[Bug] ") + "&details=";
            string body = Uri.EscapeDataString(details);
            while (head.Length + body.Length > MaxUrlLength && details.Length > 200)
            {
                details = details.Substring(0, details.Length * 9 / 10) + "\n…";
                body = Uri.EscapeDataString(details);
            }
            return head + body;
        }

        // ------------------------------------------------------------------ details

        private static string Details()
        {
            var sb = new StringBuilder();
            try
            {
                var bepinex = typeof(Paths).Assembly.GetName().Version;
                sb.AppendLine($"Replay Watcher {Plugin.Instance.Info.Metadata.Version} · BepInEx {bepinex} · Hearthstone {HsVersion()}");
                sb.AppendLine($"PC: {SystemInfo.operatingSystem} · {SystemInfo.processorType.Trim()} ({SystemInfo.processorCount} threads) · " +
                              $"{Mathf.RoundToInt(SystemInfo.systemMemorySize / 1024f)} GB RAM");
                sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName} · {Mathf.RoundToInt(SystemInfo.graphicsMemorySize / 1024f)} GB · {SystemInfo.graphicsDeviceType}");
                var rate = Screen.currentResolution.refreshRateRatio.value;
                sb.AppendLine($"Screen: {Screen.width}x{Screen.height} {(Screen.fullScreen ? "full screen" : "windowed")}, {Math.Round(rate)} Hz");
                var others = Chainloader.PluginInfos.Values.Where(p => p.Metadata.GUID != Plugin.Instance.Info.Metadata.GUID)
                    .Select(p => $"{p.Metadata.Name} {p.Metadata.Version}").ToArray();
                sb.AppendLine("Other plugins: " + (others.Length > 0 ? string.Join(", ", others) : "none"));
                sb.AppendLine("Where: " + Where());
                var moves = ReplayControl.RecentCommands();
                if (moves.Length > 0) sb.AppendLine("Last moves: " + string.Join(" · ", moves));
                var warnings = LastWarnings(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), 8);
                if (warnings.Count > 0)
                {
                    sb.AppendLine("Last warnings:");
                    foreach (string w in warnings) sb.AppendLine("  " + w);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Bug report details: " + e);
                sb.AppendLine("(details incomplete: " + e.GetType().Name + ")");
            }
            return Redact(sb.ToString().TrimEnd());
        }

        private static string Where()
        {
            if (ReplayTimeline.Active)
            {
                string id = ReplayTimeline.Previewing ? ReplayTimeline.PreviewId : ReplayLibrary.PlayingId;
                string head = ReplayTimeline.Previewing ? $"bar preview of {id}" : $"replay {id}";
                if (!ReplayTimeline.Ready) return head + " (timeline not loaded)";
                float now = ReplayTimeline.NowMs;
                float speed = ReplayTimeline.Speed;
                return $"{head} at {ReplayTimeline.Clock(now)} of {ReplayTimeline.Clock(ReplayTimeline.EndMs)}, " +
                       $"turn {ReplayTimeline.TurnAt(now)}, event {ReplayTimeline.CurrentIndex + 1}/{ReplayTimeline.Points.Length}, " +
                       (speed > 0f ? $"playing x{Mathf.RoundToInt(speed)}" : "paused") +
                       (ReplayTimeline.CombatPlaying ? ", in a fight" : "") + (Plugin.BarCompact.Value ? ", bar compact" : "");
            }
            if (ReplaysUi.PageShown) return $"Replays page ({ReplayLibrary.Current.Title}, {ReplayLibrary.Entries.Count} games)";
            return "outside a replay (" + (SceneMgr.Get()?.GetMode().ToString() ?? "?") + ")";
        }

        /// <summary>The recording of the game being watched (or previewed), or null.</summary>
        private static string GameRecording()
        {
            if (!ReplayTimeline.Active) return null;
            string id = ReplayTimeline.Previewing ? ReplayTimeline.PreviewId : ReplayLibrary.PlayingId;
            if (string.IsNullOrEmpty(id)) return null;
            return ReplayLibrary.RecordingIn(Plugin.Recordings, id) ??
                   (ReplayLibrary.HasSavedDir ? ReplayLibrary.RecordingIn(ReplayLibrary.SavedDir, id) : null) ??
                   ReplayLibrary.RecordingIn(Plugin.Logs, id);
        }

        /// <summary>The client's build, from the install's .product.db (e.g. 37.0.0.255130).</summary>
        private static string HsVersion()
        {
            try
            {
                string db = Path.Combine(Paths.GameRootPath, ".product.db");
                var m = Regex.Match(Encoding.ASCII.GetString(File.ReadAllBytes(db)), @"\d+\.\d+\.\d+\.\d{4,}");
                if (m.Success) return m.Value;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bug report: no client build: " + e.Message); }
            return global::Network.ProductVersion();
        }

        // ------------------------------------------------------------------ logs

        private static List<string> LastWarnings(string log, int n)
        {
            var found = new List<string>();
            foreach (string line in Tail(log, LogTailLines))
                if (line.StartsWith("[Warning") || line.StartsWith("[Error") || line.StartsWith("[Fatal"))
                    found.Add(line.Length > 220 ? line.Substring(0, 220) + "…" : line);
            return found.Skip(Math.Max(0, found.Count - n)).ToList();
        }

        private static void WriteTail(string log, string to)
        {
            var lines = Tail(log, LogTailLines);
            if (lines.Count == 0) return;
            try { File.WriteAllText(to, Redact(string.Join("\n", lines.ToArray())) + "\n"); }
            catch (Exception e) { Plugin.Log.LogWarning($"Bug report: can't write {Path.GetFileName(to)}: {e.Message}"); }
        }

        /// <summary>The last n lines of a log that is still being written (opened shared).</summary>
        private static List<string> Tail(string path, int n)
        {
            var lines = new Queue<string>();
            try
            {
                if (!File.Exists(path)) return new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Enqueue(line);
                        if (lines.Count > n) lines.Dequeue();
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Bug report: can't read {Path.GetFileName(path)}: {e.Message}"); }
            return lines.ToList();
        }

        /// <summary>The Windows user and machine names out of paths and texts.</summary>
        private static string Redact(string text)
        {
            foreach (var (name, mark) in new[] { (Environment.UserName, "<user>"), (Environment.MachineName, "<pc>") })
                if (!string.IsNullOrEmpty(name) && name.Length >= 2)
                    text = Regex.Replace(text, @"(?<![\w])" + Regex.Escape(name) + @"(?![\w])", mark, RegexOptions.IgnoreCase);
            return text;
        }

        private static void Prune()
        {
            try
            {
                foreach (var old in new DirectoryInfo(Plugin.Reports).GetDirectories().OrderByDescending(d => d.Name).Skip(KeepReports))
                    old.Delete(true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bug report: can't prune old reports: " + e.Message); }
        }

        // ------------------------------------------------------------------ clipboard

        /// <summary>The Windows clipboard, an image as a DIB (browsers paste it as a picture).</summary>
        private static class Clipboard
        {
            private const uint CF_DIB = 8, GMEM_MOVEABLE = 2;

            [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
            [DllImport("user32.dll")] private static extern bool EmptyClipboard();
            [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
            [DllImport("user32.dll")] private static extern bool CloseClipboard();
            [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
            [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
            [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
            [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

            public static bool SetImage(Texture2D tex)
            {
                int w = tex.width, h = tex.height, stride = (w * 3 + 3) & ~3;
                var dib = new byte[40 + stride * h];
                // BITMAPINFOHEADER: size, width, height (positive: rows bottom-up, as Unity's), planes, 24 bits, BI_RGB.
                BitConverter.GetBytes(40).CopyTo(dib, 0);
                BitConverter.GetBytes(w).CopyTo(dib, 4);
                BitConverter.GetBytes(h).CopyTo(dib, 8);
                BitConverter.GetBytes((short)1).CopyTo(dib, 12);
                BitConverter.GetBytes((short)24).CopyTo(dib, 14);
                BitConverter.GetBytes(stride * h).CopyTo(dib, 20);
                var px = tex.GetPixels32();
                for (int y = 0; y < h; y++)
                {
                    int row = 40 + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        var c = px[y * w + x];
                        int i = row + x * 3;
                        dib[i] = c.b;
                        dib[i + 1] = c.g;
                        dib[i + 2] = c.r;
                    }
                }
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(30); continue; }
                    try
                    {
                        EmptyClipboard();
                        var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dib.Length);
                        if (mem == IntPtr.Zero) return false;
                        var ptr = GlobalLock(mem);
                        Marshal.Copy(dib, 0, ptr, dib.Length);
                        GlobalUnlock(mem);
                        if (SetClipboardData(CF_DIB, mem) != IntPtr.Zero) return true; // the clipboard owns it now
                        GlobalFree(mem);
                        return false;
                    }
                    finally { CloseClipboard(); }
                }
                return false;
            }
        }
    }
}
