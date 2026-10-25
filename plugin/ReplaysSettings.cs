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
    /// The strip above the replay rows: the view switch at its left (ReplaysSaved.cs), the shown view's size in the
    /// middle ("N games, X MB", from its folder's files; counted again only when the view's rows change), or the
    /// empty list's line, the Import button in the imported view (ReplaysImport.cs), and the Settings button at its
    /// right, which opens <see cref="ReplaysOptions"/> (how many recent games are kept, the remove prompts). The
    /// texts are copies of the row's name text, as the rows' are.
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
            CreateSwitch();
            _settings = StripButton("SettingsButton", "Settings", ReplaysOptions.Show);
            _importButton = StripButton("ImportButton", "Import", PickImport);
            ShowView();
            ColourStrip();
        }

        /// <summary>A copy of the lobby's stats button on the strip, its own release actions taken off.</summary>
        private static Transform StripButton(string name, string text, Action onRelease)
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
            });
            PlaceStrip(Layout.Current);
            return button.transform;
        }

        private static void PlaceStrip(Layout l)
        {
            if (_strip == null) return;
            _strip.localPosition = _slot1.localPosition;
            PlaceStripText(_sizeText, l.SizeX, l.SizeZ, l.StripTextWidth, l, UberText.AlignmentOptions.Center);
            PlaceStripButton(_settings, l.SettingsX, l.SettingsZ, l.SettingsScale);
            PlaceStripButton(_importButton, l.ImportX, l.ImportZ, l.SettingsScale);
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
