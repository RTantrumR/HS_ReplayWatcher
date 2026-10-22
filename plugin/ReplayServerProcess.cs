using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using BepInEx;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Runs the replay server (tools/replay_server.py, library mode) for the Replays page's Watch, and the
    /// catalog scan (python -m hsserver.catalog) that keeps the page's rows current.
    ///
    /// The server runs only for a replay: started by Watch, stopped when the replay is over (or the watch fails or
    /// is cancelled), when the player leaves Battlegrounds for another mode, and when HS quits, so looking at the
    /// page never leaves a server behind. It also gets HS's process id and exits by itself if HS dies. If something
    /// already listens on the game-server port (a server started by hand), that one is used and left alone.
    /// Output goes to BepInEx/replay_server.log.
    /// </summary>
    internal static class ReplayServerProcess
    {
        private static Process _process;
        private static StreamWriter _log;
        private static bool _sceneHooked;

        public static bool Running => _process != null && !_process.HasExited;

        private static Process _scan;
        private static volatile bool _scanDone;
        private static bool _scanAgain;

        /// <summary>Catalog the recordings folder in the background (new recordings take a couple of seconds each;
        /// known ones come from the cache): writes recordings/.catalog.txt, then exits. See TakeScanDone. A scan
        /// asked for while one runs follows it (that one may have listed the folders before the change).</summary>
        public static void ScanCatalog()
        {
            if (_scan != null && !_scan.HasExited) { _scanAgain = true; return; }
            _scanAgain = false;
            var info = new ProcessStartInfo
            {
                FileName = Plugin.Python,
                Arguments = $"-m hsserver.catalog \"{Plugin.Recordings}\" --keep {Plugin.KeepGames.Value}" +
                            (ReplayLibrary.HasSavedDir ? $" --saved \"{ReplayLibrary.SavedDir}\"" : "") +
                            $" --logs \"{Plugin.Logs}\"",
                WorkingDirectory = Plugin.Server,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                _scan?.Dispose();
                _scan = new Process { StartInfo = info, EnableRaisingEvents = true };
                _scan.OutputDataReceived += (_, __) => { };
                _scan.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && (e.Data.Contains("Error") || e.Data.Contains("skipping") || e.Data.Contains("could not")))
                        Plugin.Log.LogWarning("Catalog scan: " + e.Data);
                };
                _scan.Exited += (_, __) => _scanDone = true;
                _scan.Start();
                _scan.BeginOutputReadLine();
                _scan.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Catalog scan failed to start ({info.FileName}): {e.Message}");
                _scan = null;
            }
        }

        /// <summary>True once after a catalog scan finished (the rows file is current).</summary>
        public static bool TakeScanDone()
        {
            if (!_scanDone) return false;
            _scanDone = false;
            if (_scanAgain) ScanCatalog();
            return true;
        }

        /// <summary>Start the server unless it (or another one on the same port) is already up.</summary>
        public static void EnsureRunning()
        {
            HookScenes();
            if (Running) return;
            if (PortInUse(Plugin.LocalServerPort.Value))
            {
                Plugin.Log.LogInfo($"Replay server: port {Plugin.LocalServerPort.Value} already in use, using that server");
                return;
            }
            string dir = Plugin.Server;
            var info = new ProcessStartInfo
            {
                FileName = Plugin.Python,
                Arguments = $"\"{Path.Combine(dir, "tools", "replay_server.py")}\" " +
                            $"--recordings \"{Plugin.Recordings}\" " +
                            (ReplayLibrary.HasSavedDir ? $"--saved \"{ReplayLibrary.SavedDir}\" " : "") +
                            $"--logs \"{Plugin.Logs}\" " +
                            $"--port {Plugin.LocalServerPort.Value} --control-port {Plugin.ControlPort.Value} " +
                            $"--parent-pid {Process.GetCurrentProcess().Id}",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                _log = new StreamWriter(Path.Combine(Paths.BepInExRootPath, "replay_server.log"), false) { AutoFlush = true };
                _process = new Process { StartInfo = info, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
                _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
                _process.Exited += (_, __) => Plugin.Log.LogInfo("Replay server exited");
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Plugin.Log.LogInfo($"Replay server started (pid {_process.Id}): {info.FileName} {info.Arguments}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Replay server failed to start ({info.FileName}): {e.Message}");
                _process = null;
            }
        }

        public static void Stop(string why)
        {
            if (_process == null) return;
            try
            {
                if (!_process.HasExited)
                {
                    // A venv's python.exe is a launcher that runs the real interpreter as its child: end the tree.
                    using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {_process.Id} /T /F")
                           { UseShellExecute = false, CreateNoWindow = true }))
                        kill?.WaitForExit(3000);
                    if (!_process.HasExited) _process.Kill();
                    Plugin.Log.LogInfo($"Replay server stopped ({why})");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replay server stop: " + e.Message); }
            _process.Dispose();
            _process = null;
            lock (typeof(ReplayServerProcess)) { _log?.Dispose(); _log = null; }
        }

        private static void WriteLog(string line)
        {
            if (line == null) return;
            lock (typeof(ReplayServerProcess)) { _log?.WriteLine(line); }
        }

        private static bool PortInUse(int port)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return false;
            }
            catch (SocketException) { return true; }
        }

        /// <summary>Stop on leaving Battlegrounds: the lobby (BACON) going to anything but a game or the BG collection.</summary>
        private static void HookScenes()
        {
            if (_sceneHooked || SceneMgr.Get() == null) return;
            SceneMgr.Get().RegisterScenePreUnloadEvent(OnScenePreUnload);
            _sceneHooked = true;
        }

        private static void OnScenePreUnload(SceneMgr.Mode prevMode, PegasusScene prevScene, object userData)
        {
            var next = SceneMgr.Get().GetMode();
            if (prevMode == SceneMgr.Mode.BACON && next != SceneMgr.Mode.GAMEPLAY && next != SceneMgr.Mode.BACON &&
                next != SceneMgr.Mode.BACON_COLLECTION)
                Stop($"left Battlegrounds for {next}");
        }
    }
}
