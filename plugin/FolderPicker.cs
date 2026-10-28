using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Asks the player for a folder, or for files: Windows' folder or file window, run by PowerShell in its own
    /// process (HS's Mono has no Windows Forms, and a dialog on HS's own thread would freeze the game), owned by an
    /// invisible topmost window so it comes up in front. Over a fullscreen game it may still open behind, so while
    /// it is open the game shows a popup saying so; its Cancel closes the window.
    /// </summary>
    internal static class FolderPicker
    {
        private static Process _process;
        private static AlertPopup _modal;
        private static Action<string> _done;
        private static volatile bool _exited;
        private static string _result;

        public static bool Open => _process != null;

        /// <summary>Ask for a folder (description: the folder window's line; message: the game's popup meanwhile); done
        /// gets its path, or null if the player cancelled.</summary>
        public static void Pick(string description, string message, Action<string> done)
        {
            if (Open) return;
            string dialog =
                "$d = New-Object System.Windows.Forms.FolderBrowserDialog\n" +
                "$d.Description = '" + description.Replace("'", "''") + "'\n" +
                "$d.ShowNewFolderButton = $true\n" +
                "$d.SelectedPath = [Environment]::GetFolderPath('MyDocuments')\n" +
                "if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.SelectedPath) }\n";
            Run(dialog, "Choose a folder", message, output => done(string.IsNullOrEmpty(output) ? null : output));
        }

        /// <summary>Ask for files (title: the file window's; filter: its file types, as Windows Forms writes them;
        /// opening in the Downloads folder); done gets their paths, empty if the player cancelled.</summary>
        public static void PickFiles(string title, string filter, string message, Action<List<string>> done)
        {
            if (Open) return;
            string dialog =
                "$d = New-Object System.Windows.Forms.OpenFileDialog\n" +
                "$d.Title = '" + title.Replace("'", "''") + "'\n" +
                "$d.Filter = '" + filter.Replace("'", "''") + "'\n" +
                "$d.Multiselect = $true\n" +
                "$d.InitialDirectory = Join-Path $env:USERPROFILE 'Downloads'\n" +
                "if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { $d.FileNames | ForEach-Object { [Console]::Out.WriteLine($_) } }\n";
            Run(dialog, "Choose replays", message, output =>
            {
                var files = new List<string>();
                foreach (string line in (output ?? "").Split('\n'))
                    if (line.Trim().Length > 0) files.Add(line.Trim());
                done(files);
            });
        }

        private static void Run(string dialog, string header, string message, Action<string> done)
        {
            _done = done;
            _exited = false;
            _result = null;
            string script =
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
                "Add-Type -AssemblyName System.Windows.Forms\n" +
                "$owner = New-Object System.Windows.Forms.Form -Property @{TopMost=$true; ShowInTaskbar=$false; " +
                "Opacity=0; StartPosition='CenterScreen'}\n" +
                "$owner.Show(); $owner.Activate()\n" +
                dialog +
                "$owner.Close()\n";
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand " +
                            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            try
            {
                _process = new Process { StartInfo = info, EnableRaisingEvents = true };
                var output = new StringBuilder();
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) output.Append(e.Data).Append('\n'); };
                _process.Exited += (_, __) =>
                {
                    try { _process.WaitForExit(); } catch { }
                    _result = output.ToString().Trim();
                    _exited = true;
                };
                _process.Start();
                _process.BeginOutputReadLine();
                Plugin.Log.LogInfo("Folder picker opened");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Folder picker failed to start: " + e.Message);
                _process = null;
                _done = null;
                done(null);
                return;
            }
            DialogManager.Get().ShowPopup(new AlertPopup.PopupInfo
            {
                m_headerText = header,
                m_text = message,
                m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
                m_responseCallback = (_, __) => { _modal = null; Kill(); },
            }, (dialog, _) =>
            {
                _modal = dialog as AlertPopup;
                if (!Open && _modal != null) { _modal.Hide(); _modal = null; } // the window closed before this showed
                return true;
            });
        }

        /// <summary>Per frame from Plugin.Update: hand the result over once the folder window has closed.</summary>
        public static void Update()
        {
            if (_process == null || !_exited) return;
            _process.Dispose();
            _process = null;
            if (_modal != null) { _modal.Hide(); _modal = null; }
            string path = string.IsNullOrEmpty(_result) ? null : _result;
            Plugin.Log.LogInfo("Folder picker closed: " + (path ?? "cancelled"));
            var done = _done;
            _done = null;
            done?.Invoke(path);
        }

        private static void Kill()
        {
            try { if (_process != null && !_process.HasExited) _process.Kill(); }
            catch (Exception e) { Plugin.Log.LogWarning("Folder picker: " + e.Message); }
        }
    }
}
