using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// In-game editor for the Replays page's row layout: <see cref="Plugin.EditorKey"/> (F11) while the page is
    /// open. One tab per card element (row spacing, background, hero, name, info line, minions, Watch button), a
    /// slider and fine -/+ steps per value, RGBA sliders for the colours; every change re-applies the layout to
    /// the rows on the page at once; elements can also be dragged and resized on the page itself
    /// (ReplaysUi.Drag.cs). Save writes it to the config (Library.RowLayout), Revert re-reads that,
    /// Defaults goes back to the built-in layout. Drawn with plain IMGUI like the replay panel (the client's
    /// IMGUI module is stripped, so controls are drawn by hand); clicks on it are kept from the page by
    /// <see cref="PanelInputBlock"/>.
    /// </summary>
    internal static class ReplaysEditor
    {
        private sealed class Param
        {
            public string Field, Label;
            public float Min, Max, Step;
            public Param(string field, string label, float min, float max, float step)
            { Field = field; Label = label; Min = min; Max = max; Step = step; }
        }

        private sealed class Tab
        {
            public string Name;
            public Param[] Values;
            public string Color; // a colour field of the layout, or null
        }

        private static readonly Tab[] Tabs =
        {
            new Tab { Name = "Row", Values = new[]
            {
                new Param("Pitch", "Row spacing", 5f, 40f, 0.5f), new Param("Top", "Page top offset", -20f, 40f, 0.5f),
                new Param("TextWidth", "Text box width", 5f, 60f, 1f),
                new Param("PageShadow", "Edge shadow", 0f, 1f, 0.05f),
            } },
            new Tab { Name = "Background", Color = "BgColor", Values = new[]
            {
                new Param("BgX", "X", 0f, 70f, 0.5f), new Param("BgZ", "Y", -30f, 10f, 0.25f),
                new Param("BgW", "Width", 10f, 80f, 0.5f), new Param("BgH", "Height", 4f, 40f, 0.25f),
                new Param("Border", "Border", 0f, 5f, 0.1f),
            } },
            Element("Hero", "Hero", null, 0.5f, 8f),
            new Tab { Name = "Hero power", Values = new[]
            {
                new Param("HpX", "X", -10f, 80f, 0.25f), new Param("HpZ", "Y", -30f, 10f, 0.1f),
                new Param("HpScale", "Size", 0.5f, 8f, 0.1f), new Param("HpGap", "Gap", -15f, 15f, 0.25f),
            } },
            new Tab { Name = "Trinkets", Values = new[]
            {
                new Param("TrinketX", "X", -10f, 80f, 0.25f), new Param("TrinketZ", "Y", -30f, 10f, 0.25f),
                new Param("TrinketScale", "Size", 0.5f, 8f, 0.1f), new Param("TrinketGap", "Gap", -15f, 15f, 0.25f),
            } },
            TextTab("Name"), TextTab("Place", new Param("PlaceTextLift", "Text lift", -3f, 3f, 0.05f)),
            TextTab("Date"), TextTab("Length"),
            Element("Minions", "Minion", null, 0.1f, 1.5f),
            Element("Watch", "Watch", null, 0.3f, 3f),
            Element("Star", "Star", null, 0.3f, 5f),
            Element("Trash", "Trash", null, 0.3f, 5f),
            Element("Share", "Share", null, 0.3f, 5f),
            Element("Bug", "Bug", null, 0.3f, 3f),
            new Tab { Name = "Hand", Color = "HandCountColor", Values = new[]
            {
                new Param("HandX", "X", -10f, 80f, 0.25f), new Param("HandZ", "Y", -30f, 10f, 0.1f),
                new Param("HandScale", "Size", 0.5f, 10f, 0.1f), new Param("HandTilt", "Tilt", 0f, 45f, 1f),
                new Param("HandCountScale", "Count size", 0.2f, 3f, 0.05f),
                new Param("HandCardsY", "Focused card lift", 0f, 12f, 0.25f),
                new Param("HandCardsH", "Cards height (up to 8)", 2f, 30f, 0.25f),
                new Param("HandCardsGap", "Most step (up to 8)", 1f, 20f, 0.25f),
                new Param("HandPanelPad", "Padding (up to 8)", 0f, 6f, 0.1f),
                new Param("HandManyH", "Cards height (9+)", 2f, 30f, 0.25f),
                new Param("HandManyGap", "Most step (9+)", 1f, 20f, 0.25f),
                new Param("HandManyPad", "Padding (9+)", 0f, 6f, 0.1f),
                new Param("HandCardsFan", "Cards turn", 0f, 15f, 0.5f), new Param("HandCardsArc", "Cards arc", 0f, 1f, 0.01f),
            } },
            new Tab { Name = "Hover cards", Values = new[]
            {
                new Param("CardFixed", "Fixed spot (0/1)", 0f, 1f, 1f),
                new Param("CardX", "X", -30f, 80f, 0.25f), new Param("CardZ", "Y", -30f, 20f, 0.25f),
                new Param("CardY", "Lift", 0f, 12f, 0.25f),
                new Param("FocusH", "Focused card height (all)", 5f, 45f, 0.25f),
            } },
            new Tab { Name = "Strip", Color = "StripColor", Values = new[]
            {
                new Param("ListTop", "List top", 0f, 20f, 0.25f), new Param("ScrollRows", "Wheel rows", 0.25f, 10f, 0.25f),
                new Param("SwitchX", "Switch X", -10f, 80f, 0.25f), new Param("SwitchZ", "Switch Y", -30f, 10f, 0.1f),
                new Param("SwitchScale", "Switch size", 0.3f, 3f, 0.05f),
                new Param("SizeX", "Size X", -10f, 80f, 0.25f), new Param("SizeZ", "Size Y", -30f, 10f, 0.1f),
                new Param("SettingsX", "Settings X", -10f, 80f, 0.25f), new Param("SettingsZ", "Settings Y", -30f, 10f, 0.1f),
                new Param("SettingsScale", "Settings size", 0.3f, 3f, 0.05f),
                new Param("ImportX", "Import X", -10f, 80f, 0.25f), new Param("ImportZ", "Import Y", -30f, 10f, 0.1f),
                new Param("StripTextScale", "Text size", 0.2f, 2f, 0.02f), new Param("StripTextWidth", "Text width", 5f, 60f, 0.5f),
                new Param("StripEmptyWidth", "Empty line width", 10f, 80f, 0.5f),
                new Param("StripPlate", "Plate", 0f, ReplaysUi.PlateStyles.Length - 1, 1f),
                new Param("StripPlateH", "Plate height", 1f, 8f, 0.1f), new Param("StripPlatePad", "Plate padding", 0f, 10f, 0.25f),
            } },
        };

        /// <summary>A text's tab: X, Y, Size, colour, and its plate (style, width, height).</summary>
        private static Tab TextTab(string name, params Param[] more) => new Tab
        {
            Name = name, Color = name + "Color", Values = new[]
            {
                new Param(name + "X", "X", -10f, 80f, 0.25f), new Param(name + "Z", "Y", -30f, 10f, 0.1f),
                new Param(name + "Scale", "Size", 0.2f, 2f, 0.02f),
                new Param(name + "Plate", "Plate", 0f, ReplaysUi.PlateStyles.Length - 1, 1f),
                new Param(name + "PlateW", "Plate width", 2f, 60f, 0.25f),
                new Param(name + "PlateH", "Plate height", 1f, 15f, 0.1f),
            }.Concat(more).ToArray(),
        };

        /// <summary>A tab with X, Y and Size for layout fields named prefix + X / Z / Scale.</summary>
        private static Tab Element(string name, string prefix, string color, float minScale, float maxScale) => new Tab
        {
            Name = name, Color = color, Values = new[]
            {
                new Param(prefix + "X", "X", -10f, 80f, 0.25f), new Param(prefix + "Z", "Y", -30f, 10f, 0.1f),
                new Param(prefix + "Scale", "Size", minScale, maxScale, maxScale <= 2f ? 0.02f : 0.1f),
            },
        };

        private const float RefHeight = 1080f, W = 360f, Pad = 10f, RowH = 30f;

        public static bool Visible;
        private static int _tab;
        private static string _drag; // the slider being dragged: field name, or colour field + channel
        private static bool _moving; // the panel itself, by its title line
        private static Vector2 _grab;
        private static string _status = "";
        private static Texture2D _bg, _border, _bar, _fill, _button, _buttonHover, _tabOn;
        private static GUIStyle _title, _label, _value, _buttonText;

        public static void Update()
        {
            if (!ReplaysUi.PageShown) { Visible = false; return; }
            if (Input.GetKeyDown(Plugin.EditorKey.Value))
            {
                Visible = !Visible;
                if (Visible)
                {
                    PanelInputBlock.Install();
                    PanelInputBlock.Grabs = ReplaysUi.DragWouldGrab;
                }
            }
            ReplaysUi.DragUpdate();
        }

        public static void OnGUI()
        {
            if (!Visible || !ReplaysUi.PageShown) return;
            EnsureStyles();
            ReplaysUi.DrawDragOverlay();
            float scale = Screen.height / RefHeight;
            var oldMatrix = GUI.matrix;
            var oldFont = GUI.skin.font;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                var tab = Tabs[_tab];
                int lines = 2 + (Tabs.Length + 3) / 4 + tab.Values.Length + (tab.Color != null ? 5 : 0) + 2;
                var panel = MovePanel(new Rect(Plugin.EditorX.Value, Plugin.EditorY.Value, W, lines * RowH + 2 * Pad),
                                      Screen.width / scale, Event.current);
                PanelInputBlock.ScreenRect = new Rect(panel.x * scale, Screen.height - panel.yMax * scale,
                                                      panel.width * scale, panel.height * scale);
                Draw(panel, Event.current);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Replays editor: " + e);
                Visible = false;
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.skin.font = oldFont;
            }
        }

        private static void Draw(Rect panel, Event e)
        {
            if (e.type == EventType.MouseUp) _drag = null;
            if (e.type == EventType.Repaint)
            {
                GUI.DrawTexture(panel, _bg);
                GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 2f), _border);
                GUI.DrawTexture(new Rect(panel.x, panel.yMax - 2f, panel.width, 2f), _border);
                GUI.DrawTexture(new Rect(panel.x, panel.y, 2f, panel.height), _border);
                GUI.DrawTexture(new Rect(panel.xMax - 2f, panel.y, 2f, panel.height), _border);
            }
            UseFont();
            float x = panel.x + Pad, y = panel.y + Pad, w = panel.width - 2 * Pad;
            GUI.Label(new Rect(x, y, w, RowH), "Replay card layout", _title);
            y += RowH;

            // Element tabs, four per line.
            float tw = (w - 3 * 4f) / 4f;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rect(x + (i % 4) * (tw + 4f), y + (i / 4) * RowH, tw, RowH - 4f);
                if (Button(r, Tabs[i].Name, e, i == _tab)) _tab = i;
            }
            y += ((Tabs.Length + 3) / 4) * RowH + 4f;

            var l = ReplaysUi.Layout.Current;
            var tab = Tabs[_tab];
            bool changed = false;
            foreach (var p in tab.Values)
            {
                var f = typeof(ReplaysUi.Layout).GetField(p.Field);
                float v = (float)f.GetValue(l);
                float nv = Slider(new Rect(x, y, w, RowH), p.Label, v, p.Min, p.Max, p.Step, p.Field, e);
                if (!Mathf.Approximately(nv, v)) { f.SetValue(l, nv); changed = true; }
                y += RowH;
            }

            if (tab.Color != null)
            {
                var f = typeof(ReplaysUi.Layout).GetField(tab.Color);
                string hex = (string)f.GetValue(l);
                Color c = ReplaysUi.ParseColor(hex, tab.Color == "PlaceColor" ? ReplaysUi.PlaceColor(1) : Color.white);
                string own = tab.Color == "PlaceColor" ? "by place" : "the asset's own";
                GUI.Label(new Rect(x, y, w - 90f, RowH), string.IsNullOrEmpty(hex) ? "Colour: " + own : "Colour: " + hex, _label);
                if (Button(new Rect(x + w - 86f, y + 2f, 86f, RowH - 6f), "Own colour", e, false)) { f.SetValue(l, ""); changed = true; }
                y += RowH;
                string[] names = { "Red", "Green", "Blue", "Alpha" };
                float[] ch = { c.r, c.g, c.b, c.a };
                bool colorChanged = false;
                for (int k = 0; k < 4; k++)
                {
                    float nv = Slider(new Rect(x, y, w, RowH), names[k], ch[k], 0f, 1f, 0.02f, tab.Color + k, e);
                    if (!Mathf.Approximately(nv, ch[k])) { ch[k] = nv; colorChanged = true; }
                    y += RowH;
                }
                if (colorChanged)
                {
                    f.SetValue(l, Hex(ch));
                    changed = true;
                }
            }

            y += 4f;
            float bw = (w - 8f) / 3f;
            if (Button(new Rect(x, y, bw, RowH - 4f), "Save", e, false))
            {
                Plugin.RowLayout.Value = l.Serialize();
                _status = "Saved to the config";
            }
            if (Button(new Rect(x + bw + 4f, y, bw, RowH - 4f), "Revert", e, false))
            {
                ReplaysUi.Layout.Current = ReplaysUi.Layout.Load();
                changed = true;
                _status = "Back to the saved layout";
            }
            if (Button(new Rect(x + 2 * (bw + 4f), y, bw, RowH - 4f), "Defaults", e, false))
            {
                ReplaysUi.Layout.Current = new ReplaysUi.Layout();
                changed = true;
                _status = "Built-in layout (not saved yet)";
            }
            y += RowH;
            GUI.Label(new Rect(x, y, w, RowH), _status, _label);

            if (changed)
            {
                ReplaysUi.ApplyLayout();
                if (_status.StartsWith("Saved")) _status = "";
            }
        }

        /// <summary>Drags the panel by its title line; the spot is saved to the config on release. Kept on screen.</summary>
        private static Rect MovePanel(Rect panel, float refW, Event e)
        {
            var title = new Rect(panel.x, panel.y, panel.width, Pad + RowH);
            if (_moving && !Input.GetMouseButton(0)) EndMove();
            if (e.type == EventType.MouseDown && e.button == 0 && title.Contains(e.mousePosition))
            {
                _moving = true;
                _grab = e.mousePosition - panel.position;
                e.Use();
            }
            else if (_moving && e.type == EventType.MouseDrag)
            {
                panel.position = e.mousePosition - _grab;
                e.Use();
            }
            else if (_moving && e.type == EventType.MouseUp && e.button == 0)
            {
                EndMove();
                e.Use();
            }
            panel.x = Mathf.Clamp(panel.x, 0f, Mathf.Max(0f, refW - panel.width));
            panel.y = Mathf.Clamp(panel.y, 0f, Mathf.Max(0f, RefHeight - panel.height));
            if (_moving)
            {
                // Live while dragging; written to the config file on release.
                Plugin.EditorX.ConfigFile.SaveOnConfigSet = false;
                Plugin.EditorX.Value = panel.x;
                Plugin.EditorY.Value = panel.y;
                Plugin.EditorX.ConfigFile.SaveOnConfigSet = true;
            }
            return panel;
        }

        private static void EndMove()
        {
            _moving = false;
            Plugin.EditorX.Value = Mathf.Round(Plugin.EditorX.Value);
            Plugin.EditorY.Value = Mathf.Round(Plugin.EditorY.Value);
            Plugin.EditorX.ConfigFile.Save();
        }

        /// <summary>Show the tab of an element picked on the page.</summary>
        public static void SelectTab(string name)
        {
            for (int i = 0; i < Tabs.Length; i++)
                if (Tabs[i].Name == name) _tab = i;
        }

        /// <summary>Label, a draggable bar, the value and -/+ steps; returns the (possibly) new value.</summary>
        private static float Slider(Rect r, string label, float v, float min, float max, float step, string id, Event e)
        {
            const float labelW = 108f, valueW = 52f, stepW = 26f;
            GUI.Label(new Rect(r.x, r.y, labelW, r.height), label, _label);
            var bar = new Rect(r.x + labelW, r.y + r.height / 2f - 5f, r.width - labelW - valueW - 2 * stepW - 12f, 10f);
            var hit = new Rect(bar.x, r.y, bar.width, r.height);
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition)) { _drag = id; e.Use(); }
            if (_drag == id && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                float t = Mathf.Clamp01((e.mousePosition.x - bar.x) / bar.width);
                v = Mathf.Round((min + t * (max - min)) / step) * step;
                e.Use();
            }
            if (e.type == EventType.Repaint)
            {
                GUI.DrawTexture(bar, _bar);
                float t = Mathf.Clamp01((v - min) / (max - min));
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * t, bar.height), _fill);
                GUI.DrawTexture(new Rect(bar.x + bar.width * t - 3f, bar.y - 4f, 6f, bar.height + 8f), _border);
            }
            float vx = bar.xMax + 6f;
            string shown = id.EndsWith("Plate")
                ? ReplaysUi.PlateStyles[Mathf.Clamp(Mathf.RoundToInt(v), 0, ReplaysUi.PlateStyles.Length - 1)]
                : v.ToString("0.###", CultureInfo.InvariantCulture);
            GUI.Label(new Rect(vx, r.y, valueW, r.height), shown, _value);
            if (Button(new Rect(vx + valueW, r.y + 3f, stepW, r.height - 6f), "-", e, false)) v -= step;
            if (Button(new Rect(vx + valueW + stepW + 2f, r.y + 3f, stepW, r.height - 6f), "+", e, false)) v += step;
            return Mathf.Clamp(v, min, max);
        }

        private static bool Button(Rect r, string text, Event e, bool on)
        {
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                GUI.DrawTexture(r, on ? _tabOn : hover ? _buttonHover : _button);
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), _border);
                GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), _border);
            }
            GUI.Label(r, text, _buttonText);
            if (hover && e.type == EventType.MouseDown && e.button == 0)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private static string Hex(float[] c)
        {
            var sb = new System.Text.StringBuilder("#");
            foreach (float v in c) sb.Append(Mathf.RoundToInt(Mathf.Clamp01(v) * 255f).ToString("X2"));
            return sb.ToString();
        }

        private static void UseFont()
        {
            var font = UberTextMgr.Get()?.m_FranklinGothicFont;
            if (font != null) GUI.skin.font = font;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static GUIStyle Style(int size, Color color, TextAnchor anchor)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = anchor, wordWrap = false, clipping = TextClipping.Clip };
            s.normal.textColor = color;
            return s;
        }

        private static void EnsureStyles()
        {
            if (_bg != null) return;
            _bg = Solid(new Color(0.11f, 0.075f, 0.05f, 0.94f));
            _border = Solid(new Color(0.78f, 0.6f, 0.27f, 1f));
            _bar = Solid(new Color(0.27f, 0.18f, 0.1f, 1f));
            _fill = Solid(new Color(0.62f, 0.42f, 0.13f, 1f));
            _button = Solid(new Color(0.27f, 0.18f, 0.1f, 1f));
            _buttonHover = Solid(new Color(0.45f, 0.3f, 0.14f, 1f));
            _tabOn = Solid(new Color(0.62f, 0.42f, 0.13f, 1f));
            var gold = new Color(1f, 0.83f, 0.42f);
            var parchment = new Color(0.96f, 0.91f, 0.8f);
            _title = Style(19, gold, TextAnchor.MiddleLeft);
            _label = Style(15, parchment, TextAnchor.MiddleLeft);
            _value = Style(15, gold, TextAnchor.MiddleRight);
            _buttonText = Style(15, parchment, TextAnchor.MiddleCenter);
        }
    }
}
