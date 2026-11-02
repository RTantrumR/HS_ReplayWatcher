using HsPuzzleBridge.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The strip above the replay rows: the sort dropdown at its left (saved and imported; ReplaysSort.cs), the shown
    /// view's size in the middle ("N games, X MB", from its folder's files; counted again only when the view's rows change), or the
    /// empty list's line, the Import button in the imported view (ReplaysImport.cs), and the Settings button at its
    /// right, which opens <see cref="ReplaysOptions"/> (how many recent games are kept, the remove prompts); the two
    /// buttons carry an icon left of their label (a drawn gear; a drawn tray and arrow). The texts are
    /// copies of the row's name text, as the rows' are.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static Transform _strip, _settings, _importButton;
        private static RowText _sizeText;
        private static readonly Dictionary<ReplayLibrary.View, KeyValuePair<int, string>> _sizes =
            new Dictionary<ReplayLibrary.View, KeyValuePair<int, string>>(); // view -> (its Version, the text)

        /// <summary>Real games on the page (the made-up rows of tools/seed_catalog.py aren't files).</summary>
        internal static int RealGames => ReplayLibrary.Recent.Entries.Count(e => !e.Id.Contains("_seed"));

        private static void CreateStrip()
        {
            if (_rowsParent == null || _slot1 == null || _strip != null) return;
            int layer = _slot1.gameObject.layer;
            _strip = new GameObject("ReplaysSettings").transform;
            _strip.SetParent(_rowsParent, false);
            _strip.localPosition = _slot1.localPosition;
            _strip.localRotation = _slot1.localRotation;
            _strip.localScale = _slot1.localScale;
            _strip.gameObject.layer = layer;
            _stripTextSource = Find(_slot1, "HeroName");
            if (_stripTextSource != null)
            {
                _sizeText = NewText(_stripTextSource, "ViewSize", "", _strip);
                Fit.Label(_sizeText.Text);
            }
            else Plugin.Log.LogWarning("Replays strip: the stock row has no HeroName text; the strip has no texts");
            PlaceStrip(Layout.Current);
            CreateViewControls();
            _settings = StripButton("SettingsButton", "page.settings", ReplaysOptions.Show, GearIcon);
            _importButton = StripButton("ImportButton", "page.import", PickImport, ImportIcon);
            CreateSortDropdown();
            ShowView();
            ColourStrip();
        }

        /// <summary>A copy of the lobby's stats button on the strip, its own release actions taken off, an icon left of
        /// its label (made in the label's colour); the label's key is read once the widget is ready.</summary>
        private static Transform StripButton(string name, string key, Action onRelease, Func<Color, GameObject> icon)
        {
            if (_strip == null || _statsButton == null) return null;
            var button = WidgetInstance.Create(AssetOf(_statsButton));
            button.name = name;
            button.transform.SetParent(_strip, false);
            button.gameObject.layer = _strip.gameObject.layer;
            button.RegisterReadyListener(_ =>
            {
                StripReleasedActions(button.gameObject);
                var b = button.GetComponentInChildren<UIBButton>(true);
                b.SetText(Strings.T(key));
                Fit.Label(b.m_ButtonText);
                b.AddEventListener(UIEventType.RELEASE, e => onRelease());
                // On the part the hover wiggle and the press turn and move (the label rides it too).
                AddStripIcon(b.m_RootObject != null ? b.m_RootObject.transform : button.transform, b.m_ButtonText, icon);
            });
            PlaceStrip(Layout.Current);
            return button.transform;
        }

        // ------------------------------------------------------------------ the buttons' icons

        private sealed class StripIcon
        {
            public UberText Label;
            public Vector3 LabelHome; // the label's own place, before it moved over for the icon
            public Transform Icon;
            public float Aspect; // the icon's width per height
            public bool Measured;
            // Measured with the button at rest, in its moving part's units (the icon's parent): the drawn label's
            // top-face centre and width, the label's own place there, the screen's right, the icon's turn; the
            // label's letter size before any shrinking (the icon's measure, alike in every language); the button's
            // middle and width along the screen's right (its art, the label and icon left out).
            public Vector3 Centre, LabelAt, Right;
            public float Width, Letter, Middle, Across;
            public Quaternion Turn;
            public float Box; // the label's own box (its Width as made), before the icon took its share
            public bool Narrowed, Logged;
        }

        private static readonly List<StripIcon> _stripIcons = new List<StripIcon>();

        private static void AddStripIcon(Transform anchor, UberText label, Func<Color, GameObject> make)
        {
            if (label == null || make == null) return;
            var colour = ParseColor(Layout.Current.StripIconColor, label.TextColor);
            var go = make(colour);
            if (go == null) return;
            go.transform.SetParent(anchor, false);
            LayerUtils.SetLayer(go, anchor.gameObject.layer);
            var size = go.GetComponent<MeshFilter>().sharedMesh.bounds.size;
            _stripIcons.Add(new StripIcon
            {
                Label = label, LabelHome = label.transform.localPosition, Icon = go.transform,
                Aspect = size.z > 0f ? size.x / size.z : 1f, Box = label.Width,
            });
            Plugin.Log.LogInfo($"Replays strip: {anchor.name} icon in {colour} (label {label.TextColor})");
        }

        /// <summary>Per frame: each icon and its label centred together on the button, the icon StripIconSize of the
        /// label's letter size (as set, before any shrinking: one size in every language), StripIconGap of that
        /// before the label; the label's box is the button's field (StripField of its width) less the icon and the
        /// gap, so a long language's label shrinks to the room beside the icon instead of pushing the icon onto the
        /// button's rim. Everything is kept in the units of the button's moving part (the icon's parent), so the
        /// hover wiggle and the press carry both; the drawn label is measured at its own place with the button at
        /// rest, so moving it never feeds back into the measure.</summary>
        private static void PlaceStripIcons()
        {
            var l = Layout.Current;
            foreach (var si in _stripIcons)
            {
                if (si.Icon == null || si.Label == null || !si.Label.gameObject.activeInHierarchy) continue;
                var anchor = si.Icon.parent;
                if (!si.Measured)
                {
                    si.Label.transform.localPosition = si.LabelHome;
                    Bounds text;
                    try { text = si.Label.GetTextBounds(); }
                    catch (Exception e) { Plugin.Log.LogDebug("Replays strip icon: " + e.Message); continue; }
                    bool drawn = text.size.x > 0.01f && text.size.z > 0.01f;
                    if (si.Icon.gameObject.activeSelf != drawn) si.Icon.gameObject.SetActive(drawn);
                    if (!drawn) continue;
                    float unit = Mathf.Abs(anchor.lossyScale.x);
                    if (unit <= 0f) continue;
                    si.Centre = anchor.InverseTransformPoint(new Vector3(text.center.x, text.max.y, text.center.z));
                    si.LabelAt = anchor.InverseTransformPoint(si.Label.transform.position);
                    si.Right = anchor.InverseTransformDirection(Vector3.right).normalized;
                    si.Width = text.size.x / unit;
                    si.Letter = LetterSize(si.Label) / unit;
                    var art = ButtonArt(anchor, si);
                    float textAt = Vector3.Dot(si.Centre, si.Right);
                    si.Middle = art.size.x > 0f ? Vector3.Dot(anchor.InverseTransformPoint(art.center), si.Right) : textAt;
                    si.Across = art.size.x > 0f ? art.size.x / unit : si.Box * Mathf.Abs(si.Label.transform.lossyScale.x) / unit;
                    si.Turn = Quaternion.Inverse(anchor.rotation);
                    si.Measured = true;
                    if (!si.Narrowed)
                        Plugin.Log.LogInfo($"Replays strip: {anchor.name} label '{si.Label.Text}' drawn {si.Width:0.###} x " +
                                           $"{text.size.z / unit:0.###}, letter {si.Letter:0.###}, text at {textAt:0.###}, " +
                                           $"button {si.Middle:0.###} across {si.Across:0.###}");
                }
                float h = l.StripIconSize * si.Letter, w = h * si.Aspect, gap = l.StripIconGap * h;
                if (!si.Narrowed)
                {
                    float labelUnit = Mathf.Abs(si.Label.transform.lossyScale.x);
                    if (labelUnit > 0f)
                    {
                        float room = si.Across * l.StripField - w - gap; // the label's room, in the anchor's units
                        float box = Mathf.Min(si.Box, room * Mathf.Abs(anchor.lossyScale.x) / labelUnit);
                        if (box > 0.05f && Mathf.Abs(box - si.Label.Width) > 0.001f)
                        {
                            si.Label.Width = box;
                            Fit.Now(si.Label);
                            si.Measured = false;
                        }
                    }
                    si.Narrowed = true;
                    if (!si.Measured) continue;
                }
                // The group (icon, gap, drawn label) centred on the button's middle.
                float left = si.Middle - (w + gap + si.Width) / 2f;
                float labelShift = left + w + gap + si.Width / 2f - Vector3.Dot(si.Centre, si.Right);
                si.Label.transform.position = anchor.TransformPoint(si.LabelAt + si.Right * labelShift);
                var iconAt = si.Centre + si.Right * (left + w / 2f - Vector3.Dot(si.Centre, si.Right));
                si.Icon.localPosition = iconAt;
                si.Icon.localRotation = si.Turn;
                si.Icon.localScale = Vector3.one * h;
            }
        }

        /// <summary>A label's letter size in world units as set (CharacterSize and the font's modifier), before
        /// ResizeToFit shrinks it: the same for "Import" and "Importieren".</summary>
        private static float LetterSize(UberText label)
        {
            float modifier = 1f;
            try { modifier = Traverse.Create(label).Field("m_CharacterSizeModifier").GetValue<float>(); }
            catch (Exception e) { Plugin.Log.LogDebug("Replays strip letter size: " + e.Message); }
            if (modifier <= 0f) modifier = 1f;
            return label.CharacterSize * modifier * 0.01f * Mathf.Abs(label.transform.lossyScale.x);
        }

        /// <summary>The world bounds of a strip button's own art (its renderers under the moving part, the label,
        /// the icon and glow quads left out): its middle and width place the icon and label.</summary>
        private static Bounds ButtonArt(Transform anchor, StripIcon si)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in anchor.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r.transform.IsChildOf(si.Label.transform) || r.transform.IsChildOf(si.Icon)) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
                if (!si.Narrowed && !si.Logged)
                    Plugin.Log.LogInfo($"Replays strip: {anchor.name} art {r.name} x {r.bounds.min.x:0.###}..{r.bounds.max.x:0.###}");
            }
            si.Logged = true;
            return b;
        }

        /// <summary>The import glyph, a unit tall.</summary>
        private static GameObject ImportIcon(Color colour)
        {
            var mesh = Glyph.Build("ImportIcon", Glyph.Import(Vector2.zero, 1f), wholeGradient: true, palette: Glyph.Plain(colour));
            return IconObject("ImportIcon", mesh, GlyphMaterial());
        }

        /// <summary>The settings gear, drawn as the import glyph is, a unit tall. (2026-10-27: the bottom bar's own
        /// gear, cut from its 38 px atlas cell with the pill keyed out, came out smudged.)</summary>
        private static GameObject GearIcon(Color colour)
        {
            var mesh = Glyph.Build("GearIcon", Glyph.Gear(Vector2.zero, 1f), wholeGradient: true, palette: Glyph.Plain(colour));
            return IconObject("GearIcon", mesh, GlyphMaterial());
        }

        private static GameObject IconObject(string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private static Material GlyphMaterial()
        {
            if (_glyphMaterial == null)
                _glyphMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = Texture2D.whiteTexture, name = "RowGlyph" };
            return _glyphMaterial;
        }

        private static void PlaceStrip(Layout l)
        {
            if (_strip == null) return;
            _strip.localPosition = _slot1.localPosition;
            PlaceStripText(_sizeText, l.SizeX, l.SizeZ, l.StripTextWidth, l, UberText.AlignmentOptions.Center);
            PlaceStripButton(_settings, l.SettingsX, l.SettingsZ, l.SettingsScale);
            PlaceStripButton(_importButton, l.ImportX, l.ImportZ, l.SettingsScale);
            PlaceSortDropdown(l);
        }

        private static void PlaceStripButton(Transform button, float x, float z, float scale)
        {
            if (button == null || _statsButton == null) return;
            button.localPosition = new Vector3(x, 1f, z);
            button.rotation = _statsButton.transform.rotation;
            float worldScale = _statsButton.transform.lossyScale.x * scale;
            button.localScale = Vector3.one * (worldScale / Mathf.Abs(_strip.lossyScale.x));
        }

        /// <summary>The shown view's size, or null while its list is empty: its games' files added up, kept per view
        /// until the view's rows change (a game saved, imported, removed, recorded or trimmed).</summary>
        private static string ViewSize(ReplayLibrary.View view)
        {
            if (_sizes.TryGetValue(view, out var known) && known.Key == view.Version) return known.Value;
            string folder = ReplayLibrary.FolderOf(view);
            int games = 0;
            long bytes = 0;
            if (folder != null)
                foreach (var e in view.Entries)
                {
                    string path = ReplayLibrary.RecordingIn(folder, e.Id);
                    if (path == null) continue; // a seeded row, or a file gone since the scan
                    try { bytes += new FileInfo(path).Length; games++; }
                    catch (Exception ex) { Plugin.Log.LogDebug("Replays size: " + ex.Message); }
                }
            double mb = bytes / (1024.0 * 1024.0);
            string text = games == 0 ? null : Strings.N("page.size", games, Texts.Number(mb, mb < 10 ? "0.0" : "0"));
            _sizes[view] = new KeyValuePair<int, string>(view.Version, text);
            return text;
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
            Fit.Now(t.Text);
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
        /// asset's own).</summary>
        private static void ColourStrip()
        {
            var l = Layout.Current;
            foreach (var t in new[] { _sizeText, _emptyText, _importedEmptyText })
                if (t?.Text != null) t.Text.TextColor = ParseColor(l.StripColor, ParseColor(l.LengthColor, t.Default));
        }

        /// <summary>A strip text and its plate shown or hidden together.</summary>
        private static void ShowStripText(RowText t, bool on)
        {
            if (t?.T == null) return;
            t.T.gameObject.SetActive(on);
            if (t.Plate != null) t.Plate.SetActive(on);
        }
    }
}
