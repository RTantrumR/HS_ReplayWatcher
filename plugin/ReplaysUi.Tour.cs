using HsPuzzleBridge.Localization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The screenshot tour: every surface of ours shot in every language, for the fit sweep (tools/tour.py tiles the
    /// shots). Started by a file, <c>%LOCALAPPDATA%\HS Replay Watcher\Shots\tour.txt</c> (its words: the locale codes
    /// and/or "page" / "bar", all of each when absent), so it needs no mouse or keyboard. Each language: the client
    /// switched to it in-process (<see cref="LocaleSwitch"/>; the pseudo-locale xxXX keeps the client in English),
    /// our texts relabelled (<see cref="Relabel"/>), then the surfaces opened by code and the frame read back. The
    /// client ends in the language it started in. Shots go to Shots\&lt;run&gt;\&lt;locale&gt;\.
    /// </summary>
    internal static partial class ReplaysUi
    {
        internal static string TourDir => Path.Combine(Plugin.DataDir, "Shots");
        private static readonly string[] TourLocales =
            { "xxXX", "enUS", "deDE", "frFR", "esES", "esMX", "itIT", "ptBR", "plPL", "jaJP", "koKR", "zhCN", "zhTW", "thTH" };
        private const float BarShare = 0.32f; // the top of the screen a bar shot keeps (the bar and its tooltip)
        private static bool _touring;
        private static float _nextTourPoll;

        // ------------------------------------------------------------------ relabel

        /// <summary>Every text of ours set again in the current language: the ones made once (lobby button, tabs,
        /// strip, sort menu) in place, the rows and the settings popup made again, the bar's labels at next use.</summary>
        internal static void Relabel()
        {
            try
            {
                var lobby = _replaysButton != null ? _replaysButton.GetComponentInChildren<UIBButton>(true) : null;
                if (lobby != null) { lobby.SetText(Strings.T("lobby.replays")); Fit.Label(lobby.m_ButtonText, keepLocale: true); }
                if (_tabsBuilt)
                    for (int i = 0; i < _tabTexts.Length; i++)
                        if (_tabTexts[i] != null) { _tabTexts[i].Text = Strings.T(TabKeys[i]); Fit.Now(_tabTexts[i]); }
                if (_emptyText != null) { _emptyText.Text.Text = Strings.T("page.empty.saved"); Fit.Now(_emptyText.Text); }
                if (_importedEmptyText != null) { _importedEmptyText.Text.Text = Strings.T("page.empty.imported"); Fit.Now(_importedEmptyText.Text); }
                _sizes.Clear();
                _shownVersion = -1;
                RelabelStripButton(_settings, "page.settings");
                RelabelStripButton(_importButton, "page.import");
                foreach (var si in _stripIcons)
                {
                    if (si.Label == null) continue;
                    si.Label.Width = si.Box;
                    si.Narrowed = false;
                    si.Measured = false;
                }
                if (_sort != null)
                {
                    // The items keep their places and get the new words (clearing and adding them again cloned items
                    // from a template whose selection bar the menu had lifted: the chosen one's text drew under it).
                    var items = Traverse.Create(_sort).Field("m_items").GetValue<List<DropdownMenuItem>>();
                    if (items != null)
                        for (int i = 0; i < items.Count && i < SortKeys.Length; i++)
                        {
                            items[i].SetValue(SortLabel(i), SortLabel(i));
                            Fit.Label(items[i].m_text);
                        }
                    _sort.setSelection(SortLabel(SortOf(ReplayLibrary.Current)));
                    if (_sort.m_selectedItem?.m_text != null) Fit.Label(_sort.m_selectedItem.m_text);
                }
                ReplaysOptions.Rebuild();
                foreach (var set in Sets) set.BuiltVersion = -1;
                ReplayTimeline.Relabel();
                ReplayBar.Relabel();
                Plugin.Log.LogInfo("Replays: relabelled in " + Language.Describe());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays relabel: " + e); }
        }

        private static void RelabelStripButton(Transform button, string key)
        {
            var b = button != null ? button.GetComponentInChildren<UIBButton>(true) : null;
            if (b == null) return;
            b.SetText(Strings.T(key));
            Fit.Label(b.m_ButtonText);
        }

        // ------------------------------------------------------------------ the tour

        /// <summary>Per frame from Plugin.Update: a tour.txt starts the tour (once a second at most).</summary>
        internal static void PollTour()
        {
            if (_touring || Time.unscaledTime < _nextTourPoll) return;
            _nextTourPoll = Time.unscaledTime + 1f;
            string trigger = Path.Combine(TourDir, "tour.txt");
            if (!File.Exists(trigger)) return;
            string spec;
            try { spec = File.ReadAllText(trigger); File.Delete(trigger); }
            catch (Exception e) { Plugin.Log.LogWarning("Tour: can't read the trigger: " + e.Message); return; }
            _touring = true;
            Plugin.Instance.StartCoroutine(Tour(spec ?? ""));
        }

        private static IEnumerator Tour(string spec)
        {
            var words = spec.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var locales = words.Where(w => TourLocales.Contains(w)).ToList();
            if (locales.Count == 0) locales = TourLocales.ToList();
            bool page = words.Contains("page") || !words.Contains("bar"), bar = words.Contains("bar") || !words.Contains("page");
            string original = LocaleSwitch.Current ?? Locales.English;
            string run = Path.Combine(TourDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(run);
            Plugin.Log.LogInfo($"Tour: {string.Join(" ", locales)}{(page ? " page" : "")}{(bar ? " bar" : "")} into {run} (client {original})");
            var done = new List<string>();
            yield return Run(TourSteps(locales, page, bar, run, done));
            ReplayBar.TourMouse = null;
            ReplayBar.TourEvent = -1;
            if (ReplayTimeline.Previewing) ReplayTimeline.StopPreview("tour done");
            yield return Run(LocaleSwitch.To(original, null));
            Language.Tour(null);
            Relabel();
            try { File.WriteAllText(Path.Combine(run, "done.txt"), string.Join("\n", done) + "\n", Encoding.UTF8); }
            catch (Exception e) { Plugin.Log.LogWarning("Tour: " + e.Message); }
            Plugin.Log.LogInfo($"Tour done: {done.Count} shots in {run}");
            _touring = false;
        }

        /// <summary>Runs a step and the steps it yields itself, catching at every level: a step that throws is logged
        /// and dropped, and the one that yielded it goes on. (Unity runs a yielded IEnumerator as a nested coroutine,
        /// and one that throws stops its caller for good, silently: seen 2026-11-02, the tour froze on a popup.)
        /// Unity's own waits (WaitForEndOfFrame, WaitForSecondsRealtime, null, the card data's job results) are
        /// passed to Unity.</summary>
        private static IEnumerator Run(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                object current;
                try
                {
                    if (!top.MoveNext()) { stack.Pop(); continue; }
                    current = top.Current;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Tour: a step failed ({top.GetType().Name}): {e}");
                    stack.Pop();
                    continue;
                }
                if (current is IEnumerator nested && !(current is CustomYieldInstruction)) { stack.Push(nested); continue; }
                yield return current;
            }
        }

        /// <summary>An action whose failure is logged and passed over (the shot goes on with what it shows).</summary>
        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Tour: {what}: {e.GetType().Name}: {e.Message}"); }
        }

        private static IEnumerator TourSteps(List<string> locales, bool page, bool bar, string run, List<string> done)
        {
            yield return ToPage();
            if (!PageShown) { Plugin.Log.LogWarning("Tour: the Replays page didn't open"); yield break; }
            if (page)
                foreach (string code in locales)
                {
                    bool ok = false;
                    yield return UseLanguage(code, r => ok = r);
                    if (!ok) continue;
                    yield return PageShots(Path.Combine(run, code), done);
                }
            if (bar)
            {
                ReplayLibrary.Show(ReplayLibrary.Recent);
                string id = ReplayLibrary.Recent.Entries.FirstOrDefault(e => !e.Id.Contains("_seed"))?.Id;
                if (id == null) { Plugin.Log.LogWarning("Tour: no recent game for the bar"); yield break; }
                if (!ReplayTimeline.Previewing) ReplayTimeline.TogglePreview(id);
                yield return Until(() => ReplayBar.Showing, 60f);
                if (!ReplayBar.Showing) { Plugin.Log.LogWarning("Tour: the bar preview didn't load"); yield break; }
                foreach (string code in locales)
                {
                    bool ok = false;
                    yield return UseLanguage(code, r => ok = r);
                    if (!ok) continue;
                    yield return BarShots(Path.Combine(run, code), done);
                }
                ReplayBar.TourMouse = null;
                ReplayTimeline.StopPreview("tour bar done");
            }
        }

        /// <summary>The BG lobby, then the Replays page, by code.</summary>
        private static IEnumerator ToPage()
        {
            if (PageShown) yield break;
            // A tour started with the client: the hub (or the BG lobby) first, settled.
            yield return Until(() => SceneMgr.Get() != null && !SceneMgr.Get().IsTransitioning() &&
                                     (SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB || SceneMgr.Get().GetMode() == SceneMgr.Mode.BACON), 240f);
            yield return new WaitForSecondsRealtime(3f);
            var scenes = SceneMgr.Get();
            if (scenes != null && scenes.GetMode() != SceneMgr.Mode.BACON)
            {
                scenes.SetNextMode(SceneMgr.Mode.BACON);
                yield return Until(() => BaconDisplay.Get() != null && !SceneMgr.Get().IsTransitioning(), 60f);
                yield return new WaitForSecondsRealtime(4f);
            }
            OpenPage();
            yield return Until(() => _pageReady && PageShown, 30f);
            yield return RowsBuilt();
        }

        /// <summary>The client in code's language (the pseudo-locale: in English), our catalog code's, our texts
        /// made again; ok(false) when the client can't switch to it.</summary>
        private static IEnumerator UseLanguage(string code, Action<bool> ok)
        {
            string client = Locales.Known(code) ? code : Locales.English;
            bool switched = false;
            yield return LocaleSwitch.To(client, r => switched = r);
            if (!switched) { Plugin.Log.LogWarning("Tour: skipping " + code); ok(false); yield break; }
            Language.Tour(code);
            Relabel();
            yield return RowsBuilt();
            ok(true);
        }

        /// <summary>Every view's rows built and set up again (or 12 s), then a second for their art.</summary>
        private static IEnumerator RowsBuilt()
        {
            yield return null;
            yield return Until(() => Sets.All(s => s.BuiltVersion == s.View.Version && s.Queue.Count == 0 && s.Loading == 0), 12f);
            yield return new WaitForSecondsRealtime(1.5f);
        }

        private static IEnumerator PageShots(string dir, List<string> done)
        {
            Directory.CreateDirectory(dir);
            ReplayLibrary.Show(ReplayLibrary.Recent);
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot(dir, "01-recent", 1f, done);

            UIStatus.Get()?.AddInfo(Strings.T("share.copied"));
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Shot(dir, "02-status", 1f, done);

            ReplayLibrary.Show(ReplayLibrary.Saved);
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Shot(dir, "03-saved", 1f, done);
            if (_sort != null)
            {
                _sort.onUserPressedButton();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot(dir, "04-sort-menu", 1f, done);
                _sort.onUserCancelled();
                yield return new WaitForSecondsRealtime(0.4f);
            }
            yield return EmptyShot(dir, "05-empty-saved", "05b-empty-saved-tip", _emptyText, done);

            ReplayLibrary.Show(ReplayLibrary.Imported);
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Shot(dir, "06-imported", 1f, done);
            yield return EmptyShot(dir, "07-empty-imported", "07b-empty-imported-tip", _importedEmptyText, done);

            ReplayLibrary.Show(ReplayLibrary.Recent);
            yield return new WaitForSecondsRealtime(0.8f);
            // Show throws after the popup is up (its punch animation finds no MonoBehaviour on the bare root, as in
            // play): passed over, so the shot shows the popup as a player sees it.
            Try("settings popup", ReplaysOptions.Show);
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Shot(dir, "08-settings", 1f, done);
            Try("settings tooltip", ReplaysOptions.TourTip);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Shot(dir, "08b-settings-tip", 1f, done);
            ReplaysOptions.Hide();
            yield return new WaitForSecondsRealtime(0.5f);

            yield return PopupShot(dir, "09-keep-fewer", done, () =>
                ConfirmPopup.Ask(Strings.T("options.keepFewer.title"), Strings.N("options.keepFewer.body", 3), Strings.T("options.delete"), null));
            yield return PopupShot(dir, "10-remove-imported", done, () =>
                ConfirmPopup.Ask(Strings.T("popup.removeImported.title"), Strings.T("popup.removeImported.starred"), Strings.T("confirm.remove"), null));
            yield return PopupShot(dir, "11-unsave", done, () =>
                ConfirmPopup.Ask(Strings.T("popup.unsave.title"), Strings.T("popup.unsave.staysImported"), Strings.T("confirm.remove"), null));
            yield return PopupShot(dir, "12-delete-replay", done, () =>
                ConfirmPopup.Ask(Strings.T("popup.deleteReplay.title"), Strings.N("popup.deleteReplay.body", Plugin.KeepGames.Value),
                                 Strings.T("options.delete"), null));
            yield return PopupShot(dir, "13-folder-refused", done, () =>
                Alert(Strings.T("folder.another.title"), Strings.T("folder.another.insideHs")));
            yield return PopupShot(dir, "14-folder-wait", done, () =>
                DialogManager.Get().ShowPopup(new AlertPopup.PopupInfo
                {
                    m_headerText = Strings.T("folder.pick.header"), m_text = Strings.T("folder.pick.message"),
                    m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
                }));
            yield return PopupShot(dir, "15-import-results", done, () =>
                Alert(Strings.T("import.result.some"), ImportText(SampleResults())));
            yield return PopupShot(dir, "16-bug", done, () =>
                DialogManager.Get().ShowPopup(new AlertPopup.PopupInfo
                {
                    m_headerText = Strings.T("bug.title"), m_text = BugReport.PopupText(true),
                    m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL, m_confirmText = Strings.T("bug.open"),
                    m_cancelText = GameStrings.Get("GLOBAL_CANCEL"),
                }));

            HidePage();
            yield return new WaitForSecondsRealtime(1.8f);
            yield return Shot(dir, "17-lobby", 1f, done);
            OpenPage();
            yield return Until(() => PageShown, 10f);
            yield return new WaitForSecondsRealtime(1.5f);
        }

        /// <summary>A strip's empty-list line shown where it goes (the list may not be empty: the line is what is
        /// checked), then the view as it was.</summary>
        private static IEnumerator EmptyShot(string dir, string name, string tipName, RowText empty, List<string> done)
        {
            if (empty == null) yield break;
            ShowStripText(_sizeText, false);
            ShowStripText(empty, true);
            PlaceStrip(Layout.Current);
            PlaceViewControls(Layout.Current);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Shot(dir, name, 1f, done);
            Try("empty list tooltip", TourEmptyTip);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Shot(dir, tipName, 1f, done);
            HideEmptyTip();
            Try("empty list ? click", TourEmptyTap);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot(dir, tipName.Replace("-tip", "-click").Replace("b-", "c-"), 1f, done);
            _shownVersion = -1;
            yield return null;
        }

        /// <summary>Import results as a few files would give them: two imported, one already there, one refused.</summary>
        private static List<ImportResult> SampleResults()
        {
            var games = ReplayLibrary.Recent.Entries.Where(e => !e.Id.Contains("_seed")).Take(4).ToList();
            var results = new List<ImportResult>();
            for (int i = 0; i < games.Count; i++)
            {
                var g = games[i];
                string start = g.Start ?? "";
                var r = new ImportResult { file = g.Id + ".jsonl.gz", id = g.Id, hero = g.Hero, place = g.Place, start = start };
                if (i < 2) r.result = "imported";
                else if (i == 2) { r.result = "exists"; r.code = "alreadyIn"; r.detail = "Saved"; }
                else { r.result = "refused"; r.code = "notBattlegrounds"; r.reason = "not a Battlegrounds game with a player to watch"; r.hero = ""; }
                results.Add(r);
            }
            return results;
        }

        private static IEnumerator BarShots(string dir, List<string> done)
        {
            Directory.CreateDirectory(dir);
            yield return new WaitForSecondsRealtime(0.8f);
            for (int i = 0; i < ReplayBar.ButtonCount; i++)
            {
                ReplayBar.TourMouse = ReplayBar.ButtonCentre(i);
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot(dir, $"b{i}-button", BarShare, done);
            }
            foreach (var p in TourEvents())
            {
                ReplayBar.TourMouse = ReplayBar.TrackPoint(p.Ms);
                ReplayBar.TourEvent = p.Index;
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot(dir, "e-" + Slug(p), BarShare, done);
            }
            ReplayBar.TourEvent = -1;
            if (ReplayTimeline.Gaps.Count > 0)
            {
                var g = ReplayTimeline.Gaps[0];
                ReplayBar.TourMouse = ReplayBar.TrackPoint((g.x + g.y) / 2f);
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot(dir, "g0-gap", BarShare, done);
            }
            ReplayBar.TourMouse = null;
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>One event per kind of label (the first of each), so every phrase is seen once and every language
        /// shows the same events (a sheet compares one event across the languages).</summary>
        private static IEnumerable<ReplayTimeline.Point> TourEvents()
        {
            var points = ReplayTimeline.Points;
            if (points == null) return Enumerable.Empty<ReplayTimeline.Point>();
            return points.Where(p => p != null)
                         .GroupBy(Slug)
                         .Select(g => g.OrderBy(p => p.Ms).First())
                         .OrderBy(p => p.Ms)
                         .ToList();
        }

        private static string Slug(ReplayTimeline.Point p)
        {
            string kind = p.Kind == "ce" ? "tavern" : p.Kind == "rc" ? "reconnected" : (p.Verb ?? "action");
            bool on = !string.IsNullOrEmpty(p.Target), arg = !string.IsNullOrEmpty(p.Arg);
            return (kind + (on ? "-on" : "") + (arg ? "-arg" : "")).Replace(' ', '-');
        }

        private static IEnumerator PopupShot(string dir, string name, List<string> done, Action show)
        {
            try { show(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Tour: {name}: {e.Message}"); yield break; }
            yield return Until(() => CurrentDialog() != null, 5f);
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot(dir, name, 1f, done);
            var popup = CurrentDialog() as AlertPopup;
            if (popup != null) Traverse.Create(popup).Method("ButtonPress", new[] { typeof(AlertPopup.Response) }).GetValue(AlertPopup.Response.CANCEL);
            yield return Until(() => CurrentDialog() == null, 5f);
            yield return new WaitForSecondsRealtime(0.5f);
        }

        private static DialogBase CurrentDialog()
        {
            var manager = DialogManager.Get();
            return manager == null ? null : Traverse.Create(manager).Field("m_currentDialog").GetValue<DialogBase>();
        }

        private static IEnumerator Until(Func<bool> ready, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                bool now;
                try { now = ready(); } catch { now = false; }
                if (now) yield break;
                yield return null;
            }
        }

        /// <summary>The frame as drawn (the top share of it), as a PNG (the client's Unity has no ScreenCapture
        /// texture capture: read back at the frame's end, as the bug report does).</summary>
        private static IEnumerator Shot(string dir, string name, float share, List<string> done)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                int w = Screen.width, h = Screen.height, ch = Mathf.Clamp(Mathf.RoundToInt(h * share), 1, h);
                tex = new Texture2D(w, ch, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, h - ch, w, ch), 0, 0, false);
                tex.Apply(false);
                string path = Path.Combine(dir, name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                done.Add(Path.GetFileName(dir) + "/" + name + ".png");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Tour: shot {name}: {e.Message}"); }
            finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
        }
    }
}
