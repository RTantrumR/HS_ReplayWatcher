using System;
using System.IO;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Saved and imported games: a folder the player picks outside the Hearthstone folder (Library.SavedDir), so game
    /// updates, repairs and reinstalls never touch it; its Saved and Imported folders are the page's second and third
    /// views (<see cref="ReplayLibrary.SplitSavedDir"/>).
    /// - The views go round as a carousel: an arrow on each side of the page's title (the Options menu's dropdown
    ///   arrow, turned to point sideways) shows the one before or after: recent, saved, and imported (other players'
    ///   games, and those converted from Power.log); the title names the shown one.
    /// - The star: each portrait's own favourite badge (FavoriteIconParent, the BG skin collection's heart) with
    ///   the game's StarIcon on it, moved out of the portrait into the place's tab; shown on every row: full for a
    ///   saved game, greyed otherwise; clicking it saves or unsaves.
    /// - Saving copies the recording (a recent or an imported one) into the saved folder (the first time, the folder
    ///   is asked for); the game stays in its list (a recent one until the limit takes it).
    /// - Unsaving asks first (<see cref="ConfirmPopup"/>; the settings can turn the asking off): still in the recent
    ///   or the imported list, the saved copy is removed; one saved from the imported list (FromImportedFile) moves
    ///   back there; otherwise it moves back to the recent list if that has room, else it is deleted.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static Transform _arrowPrev, _arrowNext;
        private static float _arrowUnit; // the arrow's world width at scale 1
        private static RowText _emptyText, _importedEmptyText;

        private static string CurrentTitle => ReplayLibrary.Current.Title;

        /// <summary>The views' arrows beside the title, and the empty lists' lines.</summary>
        private static void CreateViewControls()
        {
            if (_strip == null || _arrowPrev != null) return;
            var source = OptionsDropdown();
            var arrow = source != null ? source.m_button : null;
            if (arrow != null && _title != null)
            {
                _arrowPrev = ViewArrow(arrow, "ViewPrev", () => ReplayLibrary.Show(ReplayLibrary.Prev));
                _arrowNext = ViewArrow(arrow, "ViewNext", () => ReplayLibrary.Show(ReplayLibrary.Next));
            }
            else Plugin.Log.LogWarning("Replays: no dropdown arrow in the Options menu, or no title; the views have no arrows");
            if (_stripTextSource != null)
            {
                // The empty saved list's line: another copy of the row's name text, where the slider is.
                _emptyText = NewText(_stripTextSource, "SavedEmpty", "No saved replays yet. The star by a game's portrait saves it here.", _strip);
                ShowStripText(_emptyText, false);
                _importedEmptyText = NewText(_stripTextSource, "ImportedEmpty",
                                             "No imported replays yet. Import shared replays or a Power.log, or paste them here with Ctrl+V.", _strip);
                ShowStripText(_importedEmptyText, false);
            }
            PlaceViewControls(Layout.Current);
        }

        /// <summary>A copy of the dropdown's arrow button under the title's frame, clicked through its UIBButton.</summary>
        private static Transform ViewArrow(UIBButton source, string name, Action onRelease)
        {
            var go = UnityEngine.Object.Instantiate(source.gameObject, _title.transform.parent, false);
            go.name = name;
            go.SetActive(true);
            LayerUtils.SetLayer(go, _title.gameObject.layer);
            var b = go.GetComponent<UIBButton>() ?? go.GetComponentInChildren<UIBButton>(true);
            b?.AddEventListener(UIEventType.RELEASE, e => onRelease());
            go.transform.localScale = Vector3.one;
            go.transform.rotation = _title.transform.rotation;
            if (_arrowUnit <= 0f)
            {
                var size = RendererBounds(go).size;
                _arrowUnit = Mathf.Max(size.x, size.z);
            }
            return go.transform;
        }

        /// <summary>The arrows TitleArrowX of the title's frame units either side of the title and TitleArrowShift to
        /// the right (screen), TitleArrowSize world units wide, the dropdown's downward arrow turned TitleArrowRot
        /// degrees in the page's plane each way.</summary>
        private static void PlaceViewControls(Layout l)
        {
            if (_arrowPrev != null && _arrowNext != null && _title != null && _arrowUnit > 0f)
            {
                var t = _title.transform;
                foreach (var (arrow, side) in new[] { (_arrowPrev, -1f), (_arrowNext, 1f) })
                {
                    arrow.localPosition = t.localPosition + new Vector3(side * l.TitleArrowX, 0.1f, l.TitleArrowZ);
                    arrow.rotation = Quaternion.AngleAxis(-side * l.TitleArrowRot, Vector3.up) * t.rotation;
                    arrow.localScale = Vector3.one * (l.TitleArrowSize / _arrowUnit); // _arrowUnit: its width at local scale 1
                    arrow.localPosition += t.parent.InverseTransformDirection(Vector3.right) * l.TitleArrowShift;
                }
                // The frame may be turned: the one drawn further left is "before".
                if (_arrowPrev.position.x > _arrowNext.position.x)
                {
                    var p = _arrowPrev.localPosition;
                    _arrowPrev.localPosition = _arrowNext.localPosition;
                    _arrowNext.localPosition = p;
                    var r = _arrowPrev.rotation;
                    _arrowPrev.rotation = _arrowNext.rotation;
                    _arrowNext.rotation = r;
                }
            }
            foreach (var empty in new[] { _emptyText, _importedEmptyText })
                if (empty != null)
                    PlaceStripText(empty, l.SizeX, l.SizeZ, l.StripEmptyWidth, l, UberText.AlignmentOptions.Center);
        }

        /// <summary>The view's size, or the empty lines (saved or imported, none yet), and its buttons.</summary>
        private static void ShowView()
        {
            var view = ReplayLibrary.Current;
            string size = ViewSize(view);
            if (_sizeText != null && size != null) _sizeText.Text.Text = size;
            ShowStripText(_sizeText, size != null);
            ShowStripText(_emptyText, size == null && view == ReplayLibrary.Saved);
            ShowStripText(_importedEmptyText, size == null && view == ReplayLibrary.Imported);
            if (_importButton != null) _importButton.gameObject.SetActive(view == ReplayLibrary.Imported);
            ShowSortButton();
            PlaceStrip(Layout.Current); // the shown texts' plates are sized from the drawn text
            PlaceViewControls(Layout.Current);
        }

        private static ReplayLibrary.View _shownView;
        private static int _shownVersion = -1;

        /// <summary>Per frame: the view's pieces when the view or its rows change, and every row's heart.</summary>
        private static void UpdateSaved()
        {
            if (_shownView != ReplayLibrary.Current || _shownVersion != ReplayLibrary.Current.Version)
            {
                _shownView = ReplayLibrary.Current;
                _shownVersion = ReplayLibrary.Current.Version;
                ShowView();
            }
            foreach (var p in AllParts)
            {
                UpdateHeart(p);
                UpdateRowButtons(p);
            }
        }

        // ------------------------------------------------------------------ stars

        // The star (the game's StarIcon texture) drawn on the portrait's favourite badge in place of its heart.
        private const string StarTexture = "StarIcon.png:966599a9bd4f8d4468c07cbca2bf67ab";
        private static Texture2D _star, _starOff;
        private static bool _starTried;
        private static object _starHandle; // the asset handle, kept: a dropped one frees its asset

        private static Texture2D Star()
        {
            if (_star != null || _starTried) return _star;
            _starTried = true;
            try
            {
                var handle = AssetLoader.Get().LoadAsset<Texture2D>(StarTexture);
                _starHandle = handle;
                _star = handle?.Asset;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays star: " + e.Message); }
            if (_star == null) Plugin.Log.LogWarning("Replays star: no StarIcon texture; the heart stays");
            return _star;
        }

        /// <summary>The star greyed out and faded, for games that aren't saved: the badge's shader
        /// (Hero/Unlit_Transparent) has no colour to tint, so unsaved stars get this texture instead. Made once from
        /// a readable copy (the asset isn't CPU-readable).</summary>
        private static Texture2D StarOffTexture(Texture src)
        {
            if (_starOff != null || src == null) return _starOff;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            var active = RenderTexture.active;
            Graphics.Blit(src, rt, Vector2.one, Vector2.zero);
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                byte g = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
                g = (byte)(g * 3 / 4);
                px[i] = new Color32(g, g, g, (byte)(c.a * 3 / 4));
            }
            tex.SetPixels32(px);
            tex.Apply();
            _starOff = tex;
            return tex;
        }

        /// <summary>The portrait's favourite badge, on, with the star: it loads with the portrait (and its own states
        /// hide it), so it is looked for and switched on every frame. Taken out of the portrait onto the card, where
        /// the layout puts it (StarX/StarZ, its badge StarScale tall): by default at the left end of the place's tab.</summary>
        private static void UpdateHeart(RowParts p)
        {
            if (p.Root == null || p.Hero == null) return;
            if (p.Heart == null)
            {
                p.Heart = Find(p.Hero, "FavoriteIconParent");
                if (p.Heart == null) return;
                var icon = Find(p.Heart, "FavoriteIcon");
                p.HeartIcon = icon != null ? icon.GetComponent<Renderer>() : null;
                if (icon != null)
                {
                    var element = icon.GetComponent<PegUIElement>() ?? icon.gameObject.AddComponent<PegUIElement>();
                    string id = p.Id;
                    var parts = p;
                    element.AddEventListener(UIEventType.RELEASE, e => OnHeart(id));
                    // Hovered, an unsaved star shows gold, as the row's other buttons take their colour.
                    element.AddEventListener(UIEventType.ROLLOVER, e => parts.HeartHover = true);
                    element.AddEventListener(UIEventType.ROLLOUT, e => parts.HeartHover = false);
                }
                p.Heart.SetParent(p.Root, true);
                p.HeartY = p.Heart.localPosition.y;
                p.HeartUnit = p.Heart.localScale.x;
                var b = p.HeartIcon != null ? p.HeartIcon.bounds : default;
                float h = Mathf.Abs(p.Root.InverseTransformVector(b.size).z);
                if (h > 0.001f) p.HeartUnit /= h; // the badge's scale per row unit of height
            }
            var l = Layout.Current;
            p.Heart.localPosition = new Vector3(l.StarX, p.HeartY, l.StarZ);
            p.Heart.localScale = Vector3.one * (p.HeartUnit * l.StarScale);
            if (p.HeartIcon != null)
            {
                // The icon sits off the badge's origin: its centre goes where the layout says.
                var at = p.Root.InverseTransformPoint(p.HeartIcon.bounds.center);
                p.Heart.localPosition += new Vector3(l.StarX - at.x, 0f, l.StarZ - at.z);
            }
            if (!p.Heart.gameObject.activeSelf) p.Heart.gameObject.SetActive(true);
            if (p.HeartIcon == null) return;
            if (!p.HeartIcon.gameObject.activeSelf) p.HeartIcon.gameObject.SetActive(true);
            bool saved = p.Set.View == ReplayLibrary.Saved || ReplayLibrary.SavedIds.Contains(p.Id) || p.HeartHover;
            if (p.HeartSaved == saved) return;
            p.HeartSaved = saved;
            var block = new MaterialPropertyBlock();
            var star = Star();
            var shown = star == null ? null : saved ? star : StarOffTexture(star);
            if (shown != null) block.SetTexture("_MainTex", shown);
            p.HeartIcon.SetPropertyBlock(block);
        }

        private static void OnHeart(string id)
        {
            if (FolderPicker.Open) return;
            if (ReplayLibrary.Current == ReplayLibrary.Saved || ReplayLibrary.SavedIds.Contains(id)) Unsave(id);
            else Save(id);
        }

        // ------------------------------------------------------------------ save / unsave

        private static void Save(string id)
        {
            if (!ReplayLibrary.HasSavedDir)
            {
                PickSavedDir(() => Save(id));
                return;
            }
            string recent = ReplayLibrary.RecordingIn(Plugin.Recordings, id);
            string src = recent ?? ReplayLibrary.RecordingIn(ReplayLibrary.ImportedDir, id);
            if (src == null) { Plugin.Log.LogWarning("Replays: no recording to save for " + id); return; }
            string dst = Path.Combine(ReplayLibrary.SavedDir, Path.GetFileName(src));
            try
            {
                Directory.CreateDirectory(ReplayLibrary.SavedDir);
                if (!File.Exists(dst)) File.Copy(src, dst);
                if (recent == null) SetFromImported(id, true);
                Plugin.Log.LogInfo($"Replays: saved {id} to {ReplayLibrary.SavedDir}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Replays: couldn't save {id}: {e.Message}");
                Alert("Couldn't save the replay", e.Message);
                ReplayLibrary.RefreshIds();
                return;
            }
            ReplayLibrary.SetSavedRow(id, true);
            ReplayServerProcess.ScanCatalog(); // lists the copy in the saved folder's own catalog (from the cache)
        }

        /// <summary>Ask for the folder of saved and imported games; then (if the folder is taken) go on.</summary>
        private static void PickSavedDir(Action then) =>
            FolderPicker.Pick("Choose a folder for your saved and imported Hearthstone replays (outside the Hearthstone folder).",
                              "A folder window has opened (it may be behind the game). Choose a folder for your replays: " +
                              "saved and imported games are kept there, about 1 MB each.",
                              path => { if (UseSavedDir(path)) then(); });

        /// <summary>A picked folder becomes the saved folder unless it is inside the Hearthstone folder (updates and
        /// repairs can wipe that) or is the recent games' folder.</summary>
        private static bool UseSavedDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string key = Norm(full), hs = Norm(Path.Combine(Application.dataPath, ".."));
            if (key == hs || key.StartsWith(hs + Path.DirectorySeparatorChar))
            {
                Alert("Choose another folder", "That folder is inside the Hearthstone folder, which game updates and " +
                                               "repairs can clear. Please choose a folder outside it.");
                return false;
            }
            if (key == Norm(Plugin.Recordings))
            {
                Alert("Choose another folder", "That is where the recent replays are kept. Please choose another folder.");
                return false;
            }
            if (key == Norm(Plugin.Logs))
            {
                Alert("Choose another folder", "That is where converted Power.log games wait to be imported. Please choose another folder.");
                return false;
            }
            Plugin.SavedDir.Value = full;
            Plugin.Log.LogInfo("Replays: saved and imported games go to " + full);
            ReplayLibrary.SplitSavedDir();
            return true;
        }

        /// <summary>For comparing paths (Windows ignores case).</summary>
        private static string Norm(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();

        private static void Unsave(string id)
        {
            string saved = ReplayLibrary.RecordingIn(ReplayLibrary.SavedDir, id);
            if (saved == null) { ReplayLibrary.RefreshIds(); return; }
            string recentDir = Plugin.Recordings;
            if (ReplayLibrary.RecentIds.Contains(id) && ReplayLibrary.RecordingIn(recentDir, id) != null)
            {
                AskUnless(Plugin.NoAskRemove.Value, "Remove from saved?",
                          "The saved copy will be removed. The game stays in your recent replays.",
                          "Remove", () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else if (ReplayLibrary.RecordingIn(ReplayLibrary.ImportedDir, id) != null)
            {
                AskUnless(Plugin.NoAskRemove.Value, "Remove from saved?",
                          "The saved copy will be removed. The game stays in your imported replays.",
                          "Remove", () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else if (FromImported(id))
            {
                AskUnless(Plugin.NoAskRemove.Value, "Remove from saved?", "The replay will be moved back to your imported replays.",
                          "Remove", () => AfterUnsave(id, () =>
                          {
                              Directory.CreateDirectory(ReplayLibrary.ImportedDir);
                              File.Move(saved, Path.Combine(ReplayLibrary.ImportedDir, Path.GetFileName(saved)));
                          }));
            }
            else if (ReplayLibrary.RecentIds.Count >= Plugin.KeepGames.Value)
            {
                AskUnless(Plugin.NoAskRemove.Value && Plugin.NoAskDelete.Value, "Delete this replay?",
                          $"Your recent replays are full ({Plugin.KeepGames.Value} games), so this replay will be deleted.",
                          "Delete", () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else
            {
                AskUnless(Plugin.NoAskRemove.Value, "Remove from saved?", "The replay will be moved back to your recent replays.",
                          "Remove", () => AfterUnsave(id, () => File.Move(saved, Path.Combine(recentDir, Path.GetFileName(saved)))));
            }
        }

        /// <summary>Ask first (<see cref="ConfirmPopup"/>), unless the settings say not to: then just do it.</summary>
        private static void AskUnless(bool skip, string header, string text, string action, Action confirmed)
        {
            if (skip) confirmed();
            else ConfirmPopup.Ask(header, text, action, confirmed);
        }

        // The saved games that came from the imported list (one id a line, in the saved folder): unsaved once they
        // are gone from there, they go back to it, not to the recent list.
        private const string FromImportedFile = ".from_imported.txt";

        private static bool FromImported(string id)
        {
            try
            {
                string path = Path.Combine(ReplayLibrary.SavedDir, FromImportedFile);
                return File.Exists(path) && Array.IndexOf(File.ReadAllLines(path), id) >= 0;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays: can't read " + FromImportedFile + ": " + e.Message); return false; }
        }

        private static void SetFromImported(string id, bool on)
        {
            try
            {
                string path = Path.Combine(ReplayLibrary.SavedDir, FromImportedFile);
                var ids = new System.Collections.Generic.List<string>();
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                        if (line.Length > 0 && line != id) ids.Add(line);
                if (on) ids.Add(id);
                if (ids.Count > 0) File.WriteAllLines(path, ids.ToArray());
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays: can't update " + FromImportedFile + ": " + e.Message); }
        }

        private static void AfterUnsave(string id, Action change)
        {
            try
            {
                change();
                SetFromImported(id, false);
                Plugin.Log.LogInfo("Replays: unsaved " + id);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Replays: couldn't unsave {id}: {e.Message}");
                Alert("Couldn't change the replay", e.Message);
                ReplayLibrary.RefreshIds();
                return;
            }
            ReplayLibrary.SetSavedRow(id, false);
            ReplayServerProcess.ScanCatalog();
        }

        private static void Alert(string header, string text) =>
            DialogManager.Get().ShowPopup(new AlertPopup.PopupInfo
            {
                m_headerText = header, m_text = text, m_responseDisplay = AlertPopup.ResponseDisplay.OK,
            });
    }
}
