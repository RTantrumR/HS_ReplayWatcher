using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The strip above the replay rows: how many games keep their recordings (5–50, Library.KeepGames), with the
    /// storage that takes. The slider is a copy of the Options menu's master volume slider (ScrollbarControl, its
    /// thumb, bones, press element, drag collider and label all inside it), taken from the prefab asset without
    /// instantiating the menu. Lowering it below the games there are asks first (the game's own alert popup), then
    /// runs the catalog scan, which deletes the oldest; after a new game the scan keeps the limit by itself.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private const string OptionsMenuPrefab = "OptionsMenu.prefab:a6e5621068fd7c8429475b3e1a1aa991";
        private const float MbPerGame = 1.0f; // a full game (~33 min) is ~1.0 MB gzipped, see docs/recorder.md
        private const int MinKept = 5, MaxKept = 50;

        private static Transform _strip, _keepSliderT;
        private static ScrollbarControl _keepSlider;
        private static RowText _keepLabel, _keepValue;

        private static int ToKept(float unit) => MinKept + Mathf.RoundToInt(Mathf.Clamp01(unit) * (MaxKept - MinKept));
        private static float ToUnit(int kept) => (kept - MinKept) / (float)(MaxKept - MinKept);

        /// <summary>Real games on the page (the made-up rows of tools/seed_catalog.py aren't files).</summary>
        private static int RealGames => ReplayLibrary.Recent.Entries.Count(e => !e.Id.Contains("_seed"));

        private static void CreateStrip()
        {
            if (_rowsParent == null || _slot1 == null || _strip != null) return;
            ScrollbarControl src = null;
            try
            {
                var prefab = AssetLoader.Get().LoadAsset<GameObject>(OptionsMenuPrefab)?.Asset;
                src = prefab != null ? prefab.GetComponentInChildren<OptionsMenu>(true)?.m_masterVolume : null;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Replays settings: " + e.Message); }
            if (src == null) { Plugin.Log.LogWarning("Replays settings: no volume slider in the Options menu"); return; }

            int layer = _slot1.gameObject.layer;
            _strip = new GameObject("ReplaysSettings").transform;
            _strip.SetParent(_rowsParent, false);
            _strip.localPosition = _slot1.localPosition;
            _strip.localRotation = _slot1.localRotation;
            _strip.localScale = _slot1.localScale;
            _strip.gameObject.layer = layer;

            var go = UnityEngine.Object.Instantiate(src.gameObject, _strip, false);
            go.name = "KeepGamesSlider";
            go.SetActive(true);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            _keepSliderT = go.transform;
            _keepSlider = go.GetComponent<ScrollbarControl>();
            // The slider's own label is the Options menu's (built for that window: blurry and stretched here under
            // the slider's scale); the strip's texts are copies of the row's name text instead, as the rows' are.
            var sliderLabel = Find(go.transform, "MasterVolumeLabel");
            if (sliderLabel != null) sliderLabel.localScale = Vector3.zero;
            _stripTextSource = Find(_slot1, "HeroName");
            if (_stripTextSource != null)
            {
                _keepLabel = NewText(_stripTextSource, "KeepGamesLabel", "Keep the last", _strip);
                _keepValue = NewText(_stripTextSource, "KeepGamesValue", "", _strip);
            }
            else Plugin.Log.LogWarning("Replays settings: the stock row has no HeroName text; the strip has no texts");
            _keepSlider.SetUpdateHandler(v => ShowKept(ToKept(v)));
            _keepSlider.SetFinishHandler(OnKeptReleased);
            _keepSlider.SetValue(ToUnit(Plugin.KeepGames.Value));
            ShowKept(Plugin.KeepGames.Value);
            PlaceStrip(Layout.Current);
            CreateSwitch();
            ColourStrip();
        }

        private static void PlaceStrip(Layout l)
        {
            if (_strip == null) return;
            _strip.localPosition = _slot1.localPosition;
            if (_keepSliderT != null)
            {
                _keepSliderT.localPosition = new Vector3(l.SliderX, 1f, l.SliderZ);
                _keepSliderT.localRotation = Quaternion.Euler(l.SliderRotX, l.SliderRotY, l.SliderRotZ);
                _keepSliderT.localScale = new Vector3(l.SliderScale, 1f, l.SliderScale);
            }
            // The texts beside the slider's ends (its drawn extent, taken in the strip's units: the bones are the
            // thumb's travel, well inside it), the label ending before it, the value starting after it, both on
            // the track's line.
            if (_keepSliderT != null)
            {
                var bounds = RendererBounds(_keepSliderT.gameObject);
                float left = float.MaxValue, right = float.MinValue, z = 0f;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x, (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                                             (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                    var local = _strip.InverseTransformPoint(corner);
                    left = Mathf.Min(left, local.x);
                    right = Mathf.Max(right, local.x);
                    z += local.z / 8f;
                }
                PlaceStripText(_keepLabel, left - l.StripTextGap, z, l.StripTextWidth, l, UberText.AlignmentOptions.Right);
                PlaceStripText(_keepValue, right + l.StripTextGap, z, l.StripTextWidth, l, UberText.AlignmentOptions.Left);
            }
        }

        private static Transform _stripTextSource; // the stock row's HeroName text (the hidden first slot's)

        /// <summary>The strip's units per row-text unit: a row's text sits under its widget's root, the strip under
        /// the slot, and the two may be scaled differently; StripTextScale means the same as the rows' text scales.</summary>
        private static float RowTextUnit()
        {
            foreach (var p in AllParts)
                if (p.Length?.T != null && Layout.Current.LengthScale > 0f)
                    return p.Length.T.lossyScale.x / Layout.Current.LengthScale / _strip.lossyScale.x;
            return 1f;
        }

        /// <summary>A strip text at the layout's size, on the track's line z, placed by its edge: right-aligned, it
        /// ends at x; left-aligned, it starts there; centred, it is centred there. With a plate (StripPlate, as
        /// the rows' texts have), the text is centred on the plate, which is as wide as the drawn text plus the
        /// padding (at most `width`, the text's box, where it wraps); without, its box of that width is placed
        /// by the edge and the text aligned inside it.
        /// A widget's text draws at the centre of its rect, not at its transform (the stock name's rect sits well
        /// below it), so the transform is moved by that offset.</summary>
        private static void PlaceStripText(RowText t, float x, float z, float width, Layout l, UberText.AlignmentOptions align)
        {
            if (t?.T == null) return;
            var tr = t.T;
            float scale = l.StripTextScale * RowTextUnit();
            tr.localScale = Vector3.one * scale;
            t.Text.Anchor = UberText.AnchorOptions.Middle;
            t.Text.Width = width / scale;
            int style = Mathf.Clamp(Mathf.RoundToInt(l.StripPlate), 0, PlateStyles.Length - 1);
            float w = width;
            if (style != 0)
            {
                t.Text.Alignment = UberText.AlignmentOptions.Center;
                float drawn = MeasureStripText(t);
                if (drawn > 0f) w = Mathf.Min(width, drawn + 2f * l.StripPlatePad);
            }
            else t.Text.Alignment = align;
            float centre = align == UberText.AlignmentOptions.Right ? x - w / 2f
                         : align == UberText.AlignmentOptions.Left ? x + w / 2f : x;
            var textCentre = Traverse.Create(t.Text).Method("GetTextCenter").GetValue<Vector3>();
            var off = _strip.InverseTransformVector(tr.TransformVector(textCentre));
            tr.localPosition = new Vector3(centre - off.x, 0.5f, z - off.z);
            float h = l.StripPlateH * (style != 0 && t.Text.IsMultiLine() ? 2f : 1f);
            SetPlate(_strip, AnyBuiltRow(), true, t, style, new Vector3(centre, 0.35f, z), w, h);
            if (t.Plate != null && t.Plate.activeSelf != tr.gameObject.activeSelf) t.Plate.SetActive(tr.gameObject.activeSelf);
        }

        /// <summary>The drawn text's width in the strip's units (its mesh's bounds, laid flat), 0 when it can't be
        /// measured (not drawn yet, hidden): the last measure then, if any.</summary>
        private static float MeasureStripText(RowText t)
        {
            if (!t.T.gameObject.activeInHierarchy) return t.Measured;
            try
            {
                var bounds = t.Text.GetTextBounds();
                float w = bounds.size.x / Mathf.Abs(_strip.lossyScale.x);
                if (w > 0.5f && !float.IsNaN(w) && !float.IsInfinity(w)) t.Measured = w;
            }
            catch (Exception e) { Plugin.Log.LogDebug("Replays strip text measure: " + e.Message); }
            return t.Measured;
        }

        /// <summary>The strip's texts in StripColor, else as the rows' length text (its LengthColor, else the
        /// asset's own: the slider's own label was made for the Options menu's dark window).</summary>
        private static void ColourStrip()
        {
            var l = Layout.Current;
            foreach (var t in new[] { _keepLabel, _keepValue, _emptyText, _logsEmptyText })
                if (t?.Text != null) t.Text.TextColor = ParseColor(l.StripColor, ParseColor(l.LengthColor, t.Default));
        }

        /// <summary>A strip text and its plate shown or hidden together.</summary>
        private static void ShowStripText(RowText t, bool on)
        {
            if (t?.T == null) return;
            t.T.gameObject.SetActive(on);
            if (t.Plate != null) t.Plate.SetActive(on);
        }

        private static void ShowKept(int kept)
        {
            if (_keepValue == null) return;
            _keepValue.Text.Text = $"{kept} games, about {kept * MbPerGame:0} MB";
            PlaceStrip(Layout.Current); // the plate follows the text's width
        }

        private static void OnKeptReleased()
        {
            int kept = ToKept(_keepSlider.GetValue());
            _keepSlider.SetValue(ToUnit(kept));
            int was = Plugin.KeepGames.Value;
            if (kept == was) return;
            int drop = RealGames - kept;
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
                    _keepSlider.SetValue(ToUnit(Plugin.KeepGames.Value));
                    ShowKept(Plugin.KeepGames.Value);
                });
        }
    }
}
