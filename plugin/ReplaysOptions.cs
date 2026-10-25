using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The Replays page's settings popup, opened by the strip's Settings button: the game's Sound Options popup
    /// (OptionsMenu_Sound_phone, the one the PC client opens) with our settings in it.
    /// - Its parts are copies of the prefab's children (header, frame, contents, the double button), never the prefab
    ///   itself: its root's SoundOptionsMenu would tie the sliders and the checkbox to the game's sound options.
    ///   The frame's two dark insets are drawn in its texture, so the popup keeps the prefab's size.
    /// - Top inset: how many recent games keep their recordings (the master volume slider, Library.KeepGames), the
    ///   count and its storage beside it. Lowering it below the games there asks first, then scans, which deletes
    ///   the oldest.
    /// - Bottom inset: "Don't ask when removing" (Library.NoAskRemove), and, only while that is on, "Don't ask when
    ///   deleting" (Library.NoAskDelete; its tooltip says what it covers). Unticking the first unticks the second.
    /// - Buttons: Open folder (the saved and imported games' folder, in Explorer) and Done. A click outside closes it.
    /// </summary>
    internal static class ReplaysOptions
    {
        private const string SoundMenuPrefab = "OptionsMenu_Sound_phone.prefab:67dc77661d359d84ea46fbe496ee81d7";
        private const float MbPerGame = 1.0f; // a full game (~33 min) is ~1.0 MB gzipped (0.93 MB measured 2026-10-25)
        private const int MinKept = 5, MaxKept = 50;

        private static object _prefabHandle; // the asset handle, kept: a dropped one frees its asset
        private static GameObject _root;
        private static Vector3 _normalScale;
        private static ScrollbarControl _keep;
        private static UberText _keepLabel;
        private static CheckBox _noAskRemove, _noAskDelete;
        private static UberText _deleteLabel;
        private static Color _labelColor;
        private static TooltipZone _tipZone;
        private static TooltipPanel _tip;
        private static bool _failed;

        public static bool Shown => _root != null && _root.activeSelf;

        private static int ToKept(float unit) => MinKept + Mathf.RoundToInt(Mathf.Clamp01(unit) * (MaxKept - MinKept));
        private static float ToUnit(int kept) => (kept - MinKept) / (float)(MaxKept - MinKept);

        public static void Show()
        {
            if (_root == null && (_failed || !Build())) return;
            _keep.SetValue(ToUnit(Plugin.KeepGames.Value));
            ShowKept(Plugin.KeepGames.Value);
            _noAskRemove.SetChecked(Plugin.NoAskRemove.Value);
            _noAskDelete.SetChecked(Plugin.NoAskRemove.Value && Plugin.NoAskDelete.Value);
            ShowDeleteLocked();
            UniversalInputManager.Get().SetGameDialogActive(true);
            _root.SetActive(true);
            AnimationUtil.ShowWithPunch(_root, 0.01f * _normalScale, 1.1f * _normalScale, _normalScale, null, noFade: true);
        }

        public static void Hide()
        {
            if (!Shown) return;
            HideTip();
            UniversalInputManager.Get().SetGameDialogActive(false);
            _root.SetActive(false);
        }

        private static bool Build()
        {
            GameObject prefab = null;
            try
            {
                var handle = AssetLoader.Get().LoadAsset<GameObject>(SoundMenuPrefab);
                _prefabHandle = handle;
                prefab = handle?.Asset;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replay settings: " + e.Message); }
            if (prefab == null)
            {
                _failed = true;
                Plugin.Log.LogWarning("Replay settings: no Sound Options popup to build from");
                return false;
            }
            int layer = prefab.layer;
            _root = new GameObject("ReplaysOptions") { layer = layer };
            _root.transform.localPosition = prefab.transform.localPosition;
            _root.transform.localRotation = prefab.transform.localRotation;
            _root.transform.localScale = prefab.transform.localScale;
            OverlayUI.Get().AddGameObject(_root);
            _normalScale = _root.transform.localScale;
            foreach (Transform child in prefab.transform)
                UnityEngine.Object.Instantiate(child.gameObject, _root.transform, false).name = child.name;
            LayerUtils.SetLayer(_root, layer);

            var blocker = CameraUtils.CreateInputBlocker(CameraUtils.FindFirstByLayer(layer), "ReplaysOptionsInputBlocker",
                                                         _root.transform, _root.transform, 10f);
            blocker.layer = layer;
            blocker.AddComponent<PegUIElement>().AddEventListener(UIEventType.RELEASE, e => Hide());

            var t = _root.transform;
            var title = Find(Find(t, "Header"), "UberText")?.GetComponent<UberText>();
            if (title != null) title.Text = "Replay Settings";

            // Top inset: the keep slider (the master volume's) under its own label, the count and the storage in a
            // copy of that label where the Reset Defaults button was.
            var restore = Find(t, "RestoreDefaultButton");
            var music = Find(t, "MusicVolume");
            var dialog = Find(t, "DialogVolume");
            foreach (var hidden in new[] { restore, music, dialog, Find(t, "SoundEffectsVolume"), Find(t, "AmbientVolume") })
                hidden?.gameObject.SetActive(false);
            _keep = Find(t, "MasterVolume")?.GetComponent<ScrollbarControl>();
            var keepLabel = Find(t, "MasterVolumeLabel")?.GetComponent<UberText>();
            if (keepLabel != null)
            {
                keepLabel.Text = "Recent replays to keep";
                var value = UnityEngine.Object.Instantiate(keepLabel.gameObject, keepLabel.transform.parent, false);
                value.name = "KeepValue";
                // The copy brings the original's rendered text object along; UberText makes its own (as NewText).
                foreach (Transform child in value.transform)
                    if (child.name.StartsWith("UberText_RenderObject")) UnityEngine.Object.Destroy(child.gameObject);
                _keepLabel = value.GetComponent<UberText>();
                _keepLabel.Width *= 2f;
                _keepLabel.Alignment = UberText.AlignmentOptions.Center;
                if (restore != null) value.transform.position = new Vector3(restore.position.x, value.transform.position.y, restore.position.z);
            }
            if (_keep != null)
            {
                _keep.SetUpdateHandler(v => ShowKept(ToKept(v)));
                _keep.SetFinishHandler(OnKeptReleased);
            }

            // Bottom inset: the mono sound checkbox and a copy of it, both moved up from its spot at the inset's
            // bottom (in steps of the sliders' row spacing: the hidden sliders' own places are only set when the
            // game lays out its menu, so they can't be used as positions).
            var mono = Find(t, "MonoSoundCheckbox");
            _noAskDelete = mono != null ? mono.GetComponent<CheckBox>() : null;
            if (_noAskDelete != null)
            {
                var copy = UnityEngine.Object.Instantiate(mono.gameObject, mono.parent, false);
                copy.name = "NoAskRemoveCheckbox";
                mono.name = "NoAskDeleteCheckbox";
                foreach (var r in copy.GetComponentsInChildren<Transform>(true))
                    if (r.name.StartsWith("UberText_RenderObject")) UnityEngine.Object.Destroy(r.gameObject);
                if (music != null && dialog != null)
                {
                    var step = (music.position - dialog.position) * 0.6f;
                    copy.transform.position = mono.position + step * 2.2f;
                    mono.position += step;
                }
                _noAskRemove = copy.GetComponent<CheckBox>();
                _noAskRemove.SetButtonText("Don't ask when removing saved or imported replays");
                _noAskDelete.SetButtonText("Don't ask when deleting saved replays");
                // Labels three times as wide (the stock one wraps after ~25 letters); the box grows both ways
                // round its centre, so it moves right by what it gained on the left.
                foreach (var box in new[] { _noAskRemove, _noAskDelete })
                {
                    var label = box.m_uberText;
                    if (label == null) continue;
                    float w = label.Width;
                    label.Width = w * 3f;
                    label.transform.localPosition += new Vector3(w * label.transform.localScale.x, 0f, 0f);
                }
                _deleteLabel = _noAskDelete.m_uberText;
                _labelColor = _deleteLabel != null ? _deleteLabel.TextColor : Color.white;
                _noAskRemove.AddEventListener(UIEventType.RELEASE, e =>
                {
                    Plugin.NoAskRemove.Value = _noAskRemove.IsChecked();
                    if (!Plugin.NoAskRemove.Value)
                    {
                        Plugin.NoAskDelete.Value = false;
                        _noAskDelete.SetChecked(false);
                    }
                    ShowDeleteLocked();
                });
                _noAskDelete.AddEventListener(UIEventType.RELEASE, e =>
                {
                    // Locked while the first is off: the click is undone.
                    if (!Plugin.NoAskRemove.Value) _noAskDelete.SetChecked(false);
                    Plugin.NoAskDelete.Value = _noAskDelete.IsChecked();
                });
                _noAskDelete.AddEventListener(UIEventType.ROLLOVER, e => ShowTip(_noAskDelete.transform));
                _noAskDelete.AddEventListener(UIEventType.ROLLOUT, e => HideTip());
            }

            // The double button: Open folder and Done.
            Button(t, "Button_left", "ButtonText_left", "Open folder", OpenFolder);
            Button(t, "Button_right", "ButtonText_right", "Done", Hide);

            var bar = BnetBar.Get();
            var zone = bar != null && bar.m_menuButton != null ? bar.m_menuButton.GetComponent<TooltipZone>() : null;
            var tip = new GameObject("TooltipZone") { layer = layer };
            tip.transform.SetParent(t, false);
            _tipZone = tip.AddComponent<TooltipZone>();
            _tipZone.tooltipPrefab = zone != null ? zone.tooltipPrefab : null;

            _root.SetActive(false);
            bool whole = _keep != null && _keepLabel != null && _noAskRemove != null;
            if (!whole) Plugin.Log.LogWarning("Replay settings: the Sound Options popup has changed; some settings are missing");
            Plugin.Log.LogInfo("Replay settings popup built");
            return true;
        }

        private static void Button(Transform root, string name, string textName, string text, Action onRelease)
        {
            var b = Find(root, name)?.GetComponentInChildren<UIBButton>(true);
            if (b == null) { Plugin.Log.LogWarning($"Replay settings: no {name}"); return; }
            var label = Find(b.transform, textName)?.GetComponent<UberText>();
            if (label != null) label.Text = text;
            else b.SetText(text);
            b.AddEventListener(UIEventType.RELEASE, e => onRelease());
        }

        /// <summary>The second checkbox's label greyed while the first is off.</summary>
        private static void ShowDeleteLocked()
        {
            if (_deleteLabel == null) return;
            var c = _labelColor;
            _deleteLabel.TextColor = Plugin.NoAskRemove.Value ? c : new Color(c.r, c.g, c.b, c.a * 0.45f);
        }

        private static void ShowTip(Transform at)
        {
            HideTip();
            if (_tipZone == null || _tipZone.tooltipPrefab == null)
            {
                Plugin.Log.LogWarning("Replay settings: no tooltip prefab");
                return;
            }
            _tip = _tipZone.ShowTooltip("Don't ask when deleting",
                "Unstarring a saved replay that is no longer among your recent replays, while those are full, deletes it " +
                "for good. With this on, that happens without asking. Needs the box above.", 0.7f);
            if (_tip == null) return;
            // The zone leaves the panel where it was made: it goes in the bottom inset's empty right half, level
            // with the two boxes as drawn (a checkbox's parts sit well off its origin), a little towards the
            // camera, TipWidth of the frame wide.
            var tr = _tip.transform;
            LayerUtils.SetLayer(_tip.gameObject, _root.layer);
            tr.rotation = _root.transform.rotation;
            tr.localScale = Vector3.one;
            var frame = Bounds(Find(_root.transform, "OptionFrame"));
            var tip = Bounds(tr);
            if (frame.size.x > 0f && tip.size.x > 0f) tr.localScale = Vector3.one * (TipWidth * frame.size.x / tip.size.x);
            float x = frame.size.x > 0f ? frame.center.x + frame.size.x * 0.25f : at.position.x;
            float z = (Bounds(_noAskRemove.transform).center.z + Bounds(_noAskDelete.transform).center.z) / 2f;
            tr.position = new Vector3(x, at.position.y + 2f, z);
            tip = Bounds(tr);
            tr.position += new Vector3(x - tip.center.x, 0f, z - tip.center.z);
        }

        private const float TipWidth = 0.34f;

        /// <summary>The world bounds of the renderers under t (empty if none).</summary>
        private static Bounds Bounds(Transform t)
        {
            var b = new Bounds();
            bool any = false;
            if (t == null) return b;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static void HideTip()
        {
            if (_tipZone != null) _tipZone.HideTooltip();
            _tip = null;
        }

        private static void ShowKept(int kept)
        {
            if (_keepLabel != null) _keepLabel.Text = $"{kept} replays, about {kept * MbPerGame:0} MB";
        }

        private static void OnKeptReleased()
        {
            int kept = ToKept(_keep.GetValue());
            _keep.SetValue(ToUnit(kept));
            int was = Plugin.KeepGames.Value;
            if (kept == was) return;
            int drop = ReplaysUi.RealGames - kept;
            if (kept > was || drop <= 0)
            {
                Plugin.KeepGames.Value = kept;
                return;
            }
            ConfirmPopup.Ask("Keep fewer replays?",
                drop == 1 ? "The oldest replay will be deleted." : $"The {drop} oldest replays will be deleted.", "Delete",
                () =>
                {
                    Plugin.KeepGames.Value = kept;
                    ReplayServerProcess.ScanCatalog();
                },
                () =>
                {
                    _keep.SetValue(ToUnit(Plugin.KeepGames.Value));
                    ShowKept(Plugin.KeepGames.Value);
                });
        }

        /// <summary>The saved and imported games' folder in Explorer (the imported one alone while none is chosen).</summary>
        private static void OpenFolder()
        {
            string dir = ReplayLibrary.HasSavedDir ? ReplayLibrary.SavedRoot : ReplayLibrary.ImportedDir;
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = false });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replay settings: can't open " + dir + ": " + e.Message); }
        }

        private static Transform Find(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }
    }
}
