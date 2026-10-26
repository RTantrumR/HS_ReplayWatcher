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
    /// buttons carry an icon left of their label (the bottom bar's gear; a drawn tray and arrow). The texts are
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
            if (_stripTextSource != null) _sizeText = NewText(_stripTextSource, "ViewSize", "", _strip);
            else Plugin.Log.LogWarning("Replays strip: the stock row has no HeroName text; the strip has no texts");
            PlaceStrip(Layout.Current);
            CreateViewControls();
            _settings = StripButton("SettingsButton", "Settings", ReplaysOptions.Show, GearIcon);
            _importButton = StripButton("ImportButton", "Import", PickImport, ImportIcon);
            CreateSortDropdown();
            ShowView();
            ColourStrip();
        }

        /// <summary>A copy of the lobby's stats button on the strip, its own release actions taken off, an icon left of
        /// its label (made in the label's colour).</summary>
        private static Transform StripButton(string name, string text, Action onRelease, Func<Color, GameObject> icon)
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
                b.SetText(text);
                b.AddEventListener(UIEventType.RELEASE, e => onRelease());
                AddStripIcon(button.transform, b.m_ButtonText, icon);
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
            public Vector3 Centre, Size; // the drawn label at its own place (centre on its top face), button units
        }

        private static readonly List<StripIcon> _stripIcons = new List<StripIcon>();
        private static Texture2D _gear;
        private static Material _gearMaterial;

        // The bottom bar's gear (its menu button's, in the Chat_v2 atlas: x right, y down as the PNG shows it), cut
        // from inside the button's dark pill, which is keyed out.
        private static readonly RectInt GearPx = new RectInt(155, 971, 38, 35);

        private static void AddStripIcon(Transform button, UberText label, Func<Color, GameObject> make)
        {
            if (label == null || make == null) return;
            var colour = ParseColor(Layout.Current.StripIconColor, label.TextColor);
            var go = make(colour);
            if (go == null) return;
            go.transform.SetParent(button, false);
            LayerUtils.SetLayer(go, button.gameObject.layer);
            var size = go.GetComponent<MeshFilter>().sharedMesh.bounds.size;
            _stripIcons.Add(new StripIcon
            {
                Label = label, LabelHome = label.transform.localPosition, Icon = go.transform,
                Aspect = size.z > 0f ? size.x / size.z : 1f,
            });
            Plugin.Log.LogInfo($"Replays strip: {button.name} icon in {colour} (label {label.TextColor})");
        }

        /// <summary>Per frame: each icon and its label centred together where the label was, the icon
        /// StripIconSize of the drawn label's height, StripIconGap of that before the label. The drawn label is
        /// measured once, at its own place (in the button's units), so moving it never feeds back into the measure.</summary>
        private static void PlaceStripIcons()
        {
            var l = Layout.Current;
            foreach (var si in _stripIcons)
            {
                if (si.Icon == null || si.Label == null || !si.Label.gameObject.activeInHierarchy) continue;
                var button = si.Icon.parent;
                if (!si.Measured)
                {
                    si.Label.transform.localPosition = si.LabelHome;
                    Bounds text;
                    try { text = si.Label.GetTextBounds(); }
                    catch (Exception e) { Plugin.Log.LogDebug("Replays strip icon: " + e.Message); continue; }
                    bool drawn = text.size.x > 0.01f && text.size.z > 0.01f;
                    if (si.Icon.gameObject.activeSelf != drawn) si.Icon.gameObject.SetActive(drawn);
                    if (!drawn) continue;
                    si.Centre = button.InverseTransformPoint(new Vector3(text.center.x, text.max.y, text.center.z));
                    si.Size = button.InverseTransformVector(text.size);
                    si.Measured = true;
                }
                var size = button.TransformVector(si.Size);
                float tw = Mathf.Abs(size.x), h = l.StripIconSize * Mathf.Abs(size.z), w = h * si.Aspect, gap = l.StripIconGap * h;
                var centre = button.TransformPoint(si.Centre);
                si.Label.transform.localPosition = si.LabelHome;
                si.Label.transform.position += Vector3.right * ((w + gap) / 2f);
                si.Icon.position = centre - Vector3.right * ((tw + gap) / 2f);
                si.Icon.rotation = Quaternion.identity;
                float parent = Mathf.Abs(button.lossyScale.x);
                si.Icon.localScale = Vector3.one * (parent > 0f ? h / parent : h);
            }
        }

        /// <summary>The import glyph, a unit tall.</summary>
        private static GameObject ImportIcon(Color colour)
        {
            var mesh = Glyph.Build("ImportIcon", Glyph.Import(Vector2.zero, 1f), wholeGradient: true, palette: Glyph.Plain(colour));
            return IconObject("ImportIcon", mesh, GlyphMaterial());
        }

        /// <summary>The game's settings gear (see GearPx), a unit tall: a readable copy of that part of the atlas,
        /// the pill's dark inside keyed out by brightness, the gear's own shading kept in the label's colour.</summary>
        private static GameObject GearIcon(Color colour)
        {
            if (_gear == null)
            {
                var bar = BnetBar.Get();
                var menu = bar != null ? bar.m_menuButton : null;
                var background = menu != null ? Traverse.Create(menu).Field("m_defaultBackground").GetValue<MeshRenderer>() : null;
                var atlas = background != null && background.sharedMaterial != null ? background.sharedMaterial.mainTexture : null;
                if (atlas == null) { Plugin.Log.LogWarning("Replays strip: no bottom bar atlas; Settings has no gear"); return null; }
                _gear = CutGear(atlas);
            }
            if (_gearMaterial == null)
                _gearMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = _gear, name = "StripGear" };
            float aspect = GearPx.width / (float)GearPx.height;
            var mesh = new Mesh { name = "GearIcon" };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-aspect / 2f, 0f, 0.5f), new Vector3(aspect / 2f, 0f, 0.5f),
                new Vector3(aspect / 2f, 0f, -0.5f), new Vector3(-aspect / 2f, 0f, -0.5f),
            });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) });
            Color32 tint = Color.Lerp(colour, Color.white, 0.2f);
            mesh.SetColors(new List<Color32> { tint, tint, tint, tint });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return IconObject("GearIcon", mesh, _gearMaterial);
        }

        private static Texture2D CutGear(Texture atlas)
        {
            float k = atlas.width / 1024f; // GearPx is in the 1024 atlas's pixels
            int x = Mathf.RoundToInt(GearPx.x * k), y = Mathf.RoundToInt(GearPx.y * k);
            int w = Mathf.RoundToInt(GearPx.width * k), h = Mathf.RoundToInt(GearPx.height * k);
            var rt = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, RenderTextureFormat.ARGB32);
            var active = RenderTexture.active;
            Graphics.Blit(atlas, rt, Vector2.one, Vector2.zero);
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "StripGear" };
            tex.ReadPixels(new Rect(x, atlas.height - y - h, w, h), 0, 0); // the render texture's rows go up
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float lum = (c.r + c.g + c.b) / 3f;
                byte shade = (byte)(255f * Mathf.Lerp(0.65f, 1f, Mathf.Clamp01((lum - 60f) / 120f)));
                px[i] = new Color32(shade, shade, shade, (byte)(255f * Mathf.Clamp01((lum - 45f) / 35f)));
            }
            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply();
            return tex;
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
            string text = games == 0 ? null
                        : $"{games} {(games == 1 ? "game" : "games")}, {(mb < 10 ? mb.ToString("0.0") : mb.ToString("0"))} MB";
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
