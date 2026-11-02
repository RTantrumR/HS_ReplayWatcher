using HsPuzzleBridge.Localization;
using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Saved and imported games: a folder the player picks outside the Hearthstone folder (Library.SavedDir), so game
    /// updates, repairs and reinstalls never touch it; its Saved and Imported folders are the page's second and third
    /// views (<see cref="ReplayLibrary.SplitSavedDir"/>).
    /// - The views (recent, saved, and imported: other players' games, and those converted from Power.log) are tabs
    ///   in the page's title (ReplaysUi.Tabs.cs). If the page's frame isn't as expected, an arrow on each side of the
    ///   title (the Options menu's dropdown arrow, turned to point sideways) goes round them instead, the title naming
    ///   the shown one.
    /// - The star: each portrait's own favourite badge (FavoriteIconParent, the BG skin collection's heart) with
    ///   the game's StarIcon on it, moved out of the portrait into the place's tab; shown on recent and saved rows:
    ///   full for a saved game, greyed otherwise; clicking it saves or unsaves. Imported rows have the trash bin
    ///   there instead (the player has the file already; games starred before that stay saved).
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

        /// <summary>The views' tabs in the title (ReplaysUi.Tabs.cs; the arrows beside the title if the page's frame
        /// isn't as expected), and the empty lists' lines.</summary>
        private static void CreateViewControls()
        {
            if (_strip == null || _arrowPrev != null || HasTabs) return;
            bool tabs;
            try { tabs = CreateTabs(); }
            catch (Exception e) { Plugin.Log.LogWarning("Replays tabs: " + e); tabs = false; }
            if (!tabs) CreateViewArrows();
            if (_stripTextSource != null)
            {
                // The empty lists' notices: more copies of the row's name text, one line each, where the size goes;
                // the round ? beside them tells the rest.
                _emptyText = NewText(_stripTextSource, "SavedEmpty", Strings.T("page.empty.saved"), _strip);
                Fit.Label(_emptyText.Text);
                ShowStripText(_emptyText, false);
                _importedEmptyText = NewText(_stripTextSource, "ImportedEmpty", Strings.T("page.empty.imported"), _strip);
                Fit.Label(_importedEmptyText.Text);
                ShowStripText(_importedEmptyText, false);
                CreateEmptyHelp();
            }
            PlaceViewControls(Layout.Current);
        }

        // ------------------------------------------------------------------ the empty lists' ? button

        /// <summary>The BG lobby's own round ? button (its InfoButton widget), should the lobby's not be found.</summary>
        private const string InfoButtonPrefab = "InfoButton.prefab:8e5dfa80406cb834aa17883b26045ddd";

        private static WidgetInstance _emptyHelp;
        private static Quaternion _emptyHelpTurn;
        private static float _emptyHelpUnit; // the button's width in the strip's units at local scale 1 (0 = not ready)
        private static TooltipZone _emptyTipZone;
        private static TooltipPanel _emptyTip;

        /// <summary>A copy of the BG lobby's round ? button beside the empty lists' notice; hovering it shows the
        /// game's tooltip panel under it with the rest of the notice (<see cref="ShowEmptyTip"/>).</summary>
        private static void CreateEmptyHelp()
        {
            if (_strip == null || _emptyHelp != null) return;
            var lobby = LobbyInfoButton();
            string asset = (lobby != null ? AssetOf(lobby) : null) ?? InfoButtonPrefab;
            _emptyHelpTurn = lobby != null ? lobby.transform.rotation
                           : _statsButton != null ? _statsButton.transform.rotation : _strip.rotation;
            var w = WidgetInstance.Create(asset);
            w.name = "EmptyHelp";
            w.transform.SetParent(_strip, false);
            w.gameObject.layer = _strip.gameObject.layer;
            w.RegisterReadyListener(_ =>
            {
                try
                {
                    // The lobby's opens the game's Battlegrounds guide twice over: its Clickable's released state sends
                    // InfoButtonClicked up, and the button itself bubbles INFOPOPUP_show up on a tap.
                    StripReleasedActions(w.gameObject);
                    var b = w.GetComponentInChildren<UIBButton>(true);
                    if (b == null) { Plugin.Log.LogWarning("Replays: the ? button has no UIBButton"); return; }
                    Traverse.Create(b).Field("m_bubbleUpEvent").SetValue(null);
                    b.AddEventListener(UIEventType.ROLLOVER, e => ShowEmptyTip());
                    b.AddEventListener(UIEventType.ROLLOUT, e => HideEmptyTip());
                    w.transform.localScale = Vector3.one;
                    w.transform.rotation = _emptyHelpTurn;
                    var face = w.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name.StartsWith("InfoButton_mesh"));
                    var size = face != null ? face.bounds.size : RendererBounds(w.gameObject).size;
                    _emptyHelpUnit = Mathf.Max(size.x, size.z) / Mathf.Abs(_strip.lossyScale.x);
                    Plugin.Log.LogInfo($"Replays: ? button ready ({asset}, {_emptyHelpUnit:0.###} strip units at scale 1)");
                    ShowView();
                }
                catch (Exception e) { Plugin.Log.LogWarning("Replays ? button setup: " + e); }
            });
            _emptyHelp = w;
        }

        /// <summary>The BG lobby's InfoButton widget (the round ?), found above the stats button.</summary>
        private static WidgetInstance LobbyInfoButton()
        {
            for (var t = _statsButton != null ? _statsButton.transform : null; t != null; t = t.parent)
                if (t.name.StartsWith("BaconLobby"))
                {
                    var info = t.GetComponentsInChildren<WidgetInstance>(true).FirstOrDefault(w => w.name == "InfoButton");
                    if (info != null) return info;
                }
            Plugin.Log.LogInfo("Replays: no InfoButton in the BG lobby; the ? is made from its prefab");
            return null;
        }

        /// <summary>The ? EmptyHelpGap after the shown notice's plate, EmptyHelpSize wide, on its line.</summary>
        private static void PlaceEmptyHelp(Layout l, float right, bool shown)
        {
            if (_emptyHelp == null) return;
            bool on = shown && _emptyHelpUnit > 0f;
            if (_emptyHelpUnit > 0f && _emptyHelp.gameObject.activeSelf != on) _emptyHelp.gameObject.SetActive(on);
            if (!on) { HideEmptyTip(); return; }
            var t = _emptyHelp.transform;
            t.localScale = Vector3.one * (l.EmptyHelpSize / _emptyHelpUnit);
            t.rotation = _emptyHelpTurn;
            t.localPosition = new Vector3(right + l.EmptyHelpGap + l.EmptyHelpSize / 2f, 1f, l.SizeZ + l.EmptyHelpZ);
        }

        /// <summary>The game's tooltip panel under the ?: the shown list's title and its notice's rest, EmptyTipWidth
        /// strip units wide (the body wraps to it and the panel grows down, so a long language takes more lines).</summary>
        private static void ShowEmptyTip()
        {
            HideEmptyTip();
            if (_emptyHelp == null || !_emptyHelp.gameObject.activeInHierarchy) return;
            if (_emptyTipZone == null)
            {
                // The cards' keyword panel (no pointer) while the game has its manager, else the Battle.net bar's
                // menu tooltip (whose pointer, drawn into its bottom cap, points down at nothing here).
                var keyword = TooltipPanelManager.Get() != null ? TooltipPanelManager.Get().m_tooltipPanelPrefab : null;
                var bar = BnetBar.Get();
                var zone = bar != null && bar.m_menuButton != null ? bar.m_menuButton.GetComponent<TooltipZone>() : null;
                var go = new GameObject("EmptyTipZone") { layer = _strip.gameObject.layer };
                go.transform.SetParent(_strip, false);
                _emptyTipZone = go.AddComponent<TooltipZone>();
                _emptyTipZone.tooltipPrefab = keyword != null ? keyword.gameObject : zone != null ? zone.tooltipPrefab : null;
                Plugin.Log.LogInfo("Replays ? tooltip: " + (keyword != null ? "the keyword panel " + keyword.name
                                   : "the Battle.net bar's tooltip (no keyword panel manager)"));
            }
            if (_emptyTipZone.tooltipPrefab == null) { Plugin.Log.LogWarning("Replays ? tooltip: no tooltip prefab"); return; }
            bool imported = ReplayLibrary.Current == ReplayLibrary.Imported;
            _emptyTip = _emptyTipZone.ShowTooltip(
                Strings.T(imported ? "page.empty.imported.title" : "page.empty.saved.title"),
                Strings.T(imported ? "page.empty.imported.body" : "page.empty.saved.body"), 0.7f);
            if (_emptyTip == null) return;
            var l = Layout.Current;
            var tr = _emptyTip.transform;
            LayerUtils.SetLayer(_emptyTip.gameObject, _strip.gameObject.layer);
            tr.rotation = Quaternion.identity;
            tr.localScale = Vector3.one;
            var tip = RendererBounds(_emptyTip.gameObject);
            float want = l.EmptyTipWidth * Mathf.Abs(_strip.lossyScale.x);
            if (tip.size.x > 0f) tr.localScale = Vector3.one * (want / tip.size.x);
            // Its top centre just under the button, a little towards the camera (over the rows).
            var button = RendererBounds(_emptyHelp.gameObject);
            tip = RendererBounds(_emptyTip.gameObject);
            float gap = l.EmptyTipGap * Mathf.Abs(_strip.lossyScale.x);
            tr.position += new Vector3(button.center.x - tip.center.x, button.max.y + 2f - tip.min.y,
                                       button.min.z - gap - tip.max.z);
        }

        private static void HideEmptyTip()
        {
            if (_emptyTip == null) return;
            _emptyTip = null;
            try { _emptyTipZone?.HideTooltip(); } catch (Exception e) { Plugin.Log.LogDebug("Replays ? tooltip: " + e.Message); }
        }

        /// <summary>For the tour: the shown list's tooltip as on hover.</summary>
        internal static void TourEmptyTip() => ShowEmptyTip();

        /// <summary>For the tour: the ? pressed, tapped and released as a click does (it must open nothing).</summary>
        internal static void TourEmptyTap()
        {
            var b = _emptyHelp != null ? _emptyHelp.GetComponentInChildren<UIBButton>(true) : null;
            if (b == null) return;
            b.TriggerPress();
            b.TriggerTap();
            b.TriggerRelease();
        }

        private static void CreateViewArrows()
        {
            var source = OptionsDropdown();
            var arrow = source != null ? source.m_button : null;
            if (arrow != null && _title != null)
            {
                _arrowPrev = ViewArrow(arrow, "ViewPrev", () => ReplayLibrary.Show(ReplayLibrary.Prev));
                _arrowNext = ViewArrow(arrow, "ViewNext", () => ReplayLibrary.Show(ReplayLibrary.Next));
            }
            else Plugin.Log.LogWarning("Replays: no dropdown arrow in the Options menu, or no title; the views have no arrows");
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
            PlaceTabs(l);
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
            // The empty lists' notices with the ? after them, the two centred together.
            float help = _emptyHelpUnit > 0f ? l.EmptyHelpGap + l.EmptyHelpSize : 0f;
            float right = 0f;
            bool shown = false;
            if (_emptyText != null)
            {
                float r = PlaceStripText(_emptyText, l.SizeX, l.SizeZ, l.StripEmptyWidth, l, UberText.AlignmentOptions.Center, help);
                if (_emptyText.T.gameObject.activeSelf) { right = r; shown = true; }
            }
            if (_importedEmptyText != null)
            {
                float r = PlaceStripText(_importedEmptyText, l.ImportedEmptyX, l.SizeZ, l.ImportedEmptyWidth, l,
                                         UberText.AlignmentOptions.Center, help);
                if (_importedEmptyText.T.gameObject.activeSelf) { right = r; shown = true; }
            }
            PlaceEmptyHelp(l, right, shown);
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
            PlaceStripIcons();
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
        /// the layout puts it (StarX/StarZ, its badge StarScale tall): by default at the left end of the place's tab.
        /// Imported rows have none (taken out of the portrait all the same, then hidden).</summary>
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
            // An imported game has no star: it came as a file the player already has; its trash bin takes the spot.
            if (p.Set.View == ReplayLibrary.Imported)
            {
                if (p.Heart.gameObject.activeSelf) p.Heart.gameObject.SetActive(false);
                return;
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
                Alert(Strings.T("alert.cantSave"), e.Message);
                ReplayLibrary.RefreshIds();
                return;
            }
            ReplayLibrary.SetSavedRow(id, true);
            ReplayServerProcess.ScanCatalog(); // lists the copy in the saved folder's own catalog (from the cache)
        }

        /// <summary>Ask for the folder of saved and imported games; then (if the folder is taken) go on.</summary>
        private static void PickSavedDir(Action then) =>
            FolderPicker.Pick(Strings.T("folder.pick.description"), Strings.T("folder.pick.message"),
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
                Alert(Strings.T("folder.another.title"), Strings.T("folder.another.insideHs"));
                return false;
            }
            if (key == Norm(Plugin.Recordings))
            {
                Alert(Strings.T("folder.another.title"), Strings.T("folder.another.recent"));
                return false;
            }
            if (key == Norm(Plugin.Logs))
            {
                Alert(Strings.T("folder.another.title"), Strings.T("folder.another.logs"));
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
                AskUnless(Plugin.NoAskRemove.Value, Strings.T("popup.unsave.title"), Strings.T("popup.unsave.staysRecent"),
                          Strings.T("confirm.remove"), () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else if (ReplayLibrary.RecordingIn(ReplayLibrary.ImportedDir, id) != null)
            {
                AskUnless(Plugin.NoAskRemove.Value, Strings.T("popup.unsave.title"), Strings.T("popup.unsave.staysImported"),
                          Strings.T("confirm.remove"), () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else if (FromImported(id))
            {
                AskUnless(Plugin.NoAskRemove.Value, Strings.T("popup.unsave.title"), Strings.T("popup.unsave.backToImported"),
                          Strings.T("confirm.remove"), () => AfterUnsave(id, () =>
                          {
                              Directory.CreateDirectory(ReplayLibrary.ImportedDir);
                              File.Move(saved, Path.Combine(ReplayLibrary.ImportedDir, Path.GetFileName(saved)));
                          }));
            }
            else if (ReplayLibrary.RecentIds.Count >= Plugin.KeepGames.Value)
            {
                AskUnless(Plugin.NoAskRemove.Value && Plugin.NoAskDelete.Value, Strings.T("popup.deleteReplay.title"),
                          Strings.N("popup.deleteReplay.body", Plugin.KeepGames.Value),
                          Strings.T("options.delete"), () => AfterUnsave(id, () => File.Delete(saved)));
            }
            else
            {
                AskUnless(Plugin.NoAskRemove.Value, Strings.T("popup.unsave.title"), Strings.T("popup.unsave.backToRecent"),
                          Strings.T("confirm.remove"), () => AfterUnsave(id, () => File.Move(saved, Path.Combine(recentDir, Path.GetFileName(saved)))));
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
                Alert(Strings.T("alert.cantChange"), e.Message);
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
