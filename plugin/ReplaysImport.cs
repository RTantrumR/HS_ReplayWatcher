using HsPuzzleBridge.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Importing replays into the Imported folder: files other players shared (their share button's copies) and
    /// Power.log files. Three ways in: the Imported view's Import button (Windows' file window), Ctrl+V on the page
    /// with files copied (in Explorer; only those named as replays or logs are taken), or files dropped into the
    /// folder (the next scan lists them; those are the player's own doing and aren't checked here).
    /// - With no folder chosen yet, the folder is asked for first (the star's prompt).
    /// - hsserver.importer does the work in its own process (isolated, -I, with the installed Python): every file
    ///   is checked before anything else reads it and stored as a copy written anew (see its docstring); one JSON
    ///   line per result comes back, and a popup lists them. Then the catalog is scanned and the Imported view shown.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static readonly string[] ImportTypes = { ".jsonl.gz", ".jsonl", ".gz", ".log" };
        private static Process _import;
        private static volatile bool _importDone;
        private static readonly List<string> _importLines = new List<string>();

        [Serializable]
        private sealed class ImportResult
        {
            public string file = "", result = "", id = "", reason = "", code = "", detail = "", hero = "", start = "";
            public int place = 0; // filled by JsonUtility
        }

        /// <summary>A result's game as the page names it ("Hero, 2nd place, Oct 20, 12:00"), else its file's name
        /// (the popup's font has no underscore: they show as spaces).</summary>
        private static string ImportedName(ImportResult r)
        {
            if (string.IsNullOrEmpty(r.hero)) return r.file.Replace('_', ' ');
            string hero = r.hero;
            try { hero = DefLoader.Get()?.GetEntityDef(r.hero)?.GetName() ?? r.hero; } catch { }
            string when = Texts.Date(r.start);
            return r.place > 0 ? Strings.F("import.name", hero, Texts.Place(r.place), when) : Strings.F("import.nameNoPlace", hero, when);
        }

        private static void PickImport() =>
            FolderPicker.PickFiles(Strings.T("import.dialog.title"), Strings.T("import.dialog.filter"), Strings.T("import.dialog.message"),
                files => { if (files.Count > 0) Import(files); });

        /// <summary>Ctrl+V on the page: the copied files named as replays or logs are imported.</summary>
        private static void PasteImport()
        {
            var files = FileClipboard.GetFiles();
            if (files.Count == 0) { ImportStatus(Strings.T("import.paste.nothing"), true); return; }
            var wanted = files.Where(f => ImportTypes.Any(t => f.EndsWith(t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (wanted.Count == 0) { ImportStatus(Strings.T("import.paste.notReplays"), true); return; }
            Import(wanted);
        }

        private static void Import(List<string> files)
        {
            if (_import != null) return;
            if (!ReplayLibrary.HasSavedDir)
            {
                PickSavedDir(() => Import(files));
                return;
            }
            ReplayLibrary.SplitSavedDir();
            var args = new StringBuilder();
            // -I keeps the source folders' files from ever being imported as Python modules; the installed Python
            // finds the server through its ._pth file. A Python set in the config may need the working directory.
            if (Plugin.PythonExe.Value.Length == 0) args.Append("-I ");
            args.Append($"-m hsserver.importer --into \"{ReplayLibrary.ImportedDir}\" --known \"{ReplayLibrary.SavedDir}\" " +
                        $"--known \"{Plugin.Recordings}\"");
            foreach (string f in files) args.Append(" \"").Append(f).Append('"');
            var start = new ProcessStartInfo
            {
                FileName = Plugin.Python,
                Arguments = args.ToString(),
                WorkingDirectory = Plugin.Server,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            lock (_importLines) _importLines.Clear();
            _importDone = false;
            try
            {
                _import = new Process { StartInfo = start, EnableRaisingEvents = true };
                _import.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_importLines) _importLines.Add(e.Data); };
                _import.ErrorDataReceived += (_, e) => { if (e.Data != null) Plugin.Log.LogDebug("Import: " + e.Data); };
                _import.Exited += (_, __) => _importDone = true;
                _import.Start();
                _import.BeginOutputReadLine();
                _import.BeginErrorReadLine();
                Plugin.Log.LogInfo($"Import: {files.Count} file(s)");
                ImportStatus(Strings.N("import.running", files.Count), false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Import: didn't start ({start.FileName}): {e.Message}");
                _import = null;
                Alert(Strings.T("alert.cantImport"), e.Message);
            }
        }

        /// <summary>Per frame from Plugin.Update: the import finished, its results shown (on this thread).</summary>
        internal static void UpdateImport()
        {
            if (_import == null || !_importDone) return;
            _import.WaitForExit(); // the output's last lines
            _import.Dispose();
            _import = null;
            List<string> lines;
            lock (_importLines) lines = new List<string>(_importLines);
            var results = new List<ImportResult>();
            foreach (string line in lines)
            {
                try { results.Add(JsonUtility.FromJson<ImportResult>(line)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Import: unreadable result {line}: {e.Message}"); }
            }
            foreach (var r in results) Plugin.Log.LogInfo($"Import: {r.file}: {r.result} {r.id} {r.reason}");
            int imported = results.Count(r => r.result == "imported");
            Alert(Strings.T(imported > 0 ? "import.result.some" : "import.result.none"), ImportText(results));
            if (imported > 0)
            {
                ReplayServerProcess.ScanCatalog();
                ReplayLibrary.Show(ReplayLibrary.Imported);
            }
        }

        /// <summary>The results popup's text: the imported, those already there and those refused (with why).</summary>
        private static string ImportText(List<ImportResult> results)
        {
            int imported = results.Count(r => r.result == "imported");
            var text = new StringBuilder();
            void Section(string title, IEnumerable<ImportResult> rs, Func<ImportResult, string> line)
            {
                var list = rs.ToList();
                if (list.Count == 0) return;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(title);
                foreach (var r in list.Take(6)) text.Append("\n").Append(line(r));
                if (list.Count > 6) text.Append("\n").Append(Strings.F("import.more", list.Count - 6));
            }
            Section(Strings.N("import.section.imported", imported), results.Where(r => r.result == "imported"), ImportedName);
            Section(Strings.T("import.section.exists"), results.Where(r => r.result == "exists"), ImportedName);
            Section(Strings.T("import.section.refused"), results.Where(r => r.result == "refused"),
                    r => Strings.F("import.refused", ImportedName(r), Reason(r)));
            if (results.Count == 0) text.Append(Strings.T("import.noAnswer"));
            return text.ToString();
        }

        /// <summary>Why a file was refused, in the language (the importer's code), else its English reason (an older
        /// server, or a code the catalogs don't know).</summary>
        private static string Reason(ImportResult r)
        {
            string key = "import.reason." + r.code;
            return !string.IsNullOrEmpty(r.code) && Strings.Has(key) ? Strings.F(key, r.detail) : r.reason;
        }

        private static void ImportStatus(string text, bool error)
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
