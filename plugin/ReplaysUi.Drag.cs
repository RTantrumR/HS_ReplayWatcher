using System;
using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Direct editing of the replay card while the layout editor (F11) is open: click an element on any row to
    /// select it (the editor switches to its tab), drag it to move it, drag the handle on its top-right corner to
    /// resize it. Every row shares one layout, so editing any row moves that element on all of them.
    ///
    /// Positions are worked out in the row root's own plane (x right, z up, the layout's units): the mouse ray
    /// from the page's camera hits that plane, and element outlines are the row-space boxes of their renderers.
    /// Clicks that grab an element are kept from the page by <see cref="PanelInputBlock"/> (the Watch button
    /// would otherwise start a replay).
    /// </summary>
    internal static partial class ReplaysUi
    {
        /// <summary>One draggable element: its editor tab, the layout fields it moves and resizes, and its objects.
        /// Size is a uniform scale; W/H (background, a text's plate) resize with the bottom-left corner kept.</summary>
        private sealed class DragTarget
        {
            public string Tab, X, Z, Size, W, H;
            public Func<RowParts, IEnumerable<Transform>> Objects;
        }

        private static readonly DragTarget[] DragTargets =
        {
            new DragTarget { Tab = "Background", X = "BgX", Z = "BgZ", W = "BgW", H = "BgH", Objects = p => new[] { p.Bg } },
            new DragTarget { Tab = "Hero", X = "HeroX", Z = "HeroZ", Size = "HeroScale", Objects = p => new[] { p.Hero } },
            new DragTarget { Tab = "Hero power", X = "HpX", Z = "HpZ", Size = "HpScale", Objects = p => new[] { p.HeroPower } },
            new DragTarget { Tab = "Trinkets", X = "TrinketX", Z = "TrinketZ", Size = "TrinketScale",
                             Objects = p => new[] { p.Trinket1, p.Trinket2 } },
            TextTarget("Name", p => p.Name), TextTarget("Place", p => p.PlaceText),
            TextTarget("Date", p => p.Date), TextTarget("Length", p => p.Length),
            new DragTarget { Tab = "Minions", X = "MinionX", Z = "MinionZ", Size = "MinionScale", Objects = p => new[] { p.Minions } },
            new DragTarget { Tab = "Watch", X = "WatchX", Z = "WatchZ", Size = "WatchScale", Objects = p => new[] { p.Watch } },
            new DragTarget { Tab = "Star", X = "StarX", Z = "StarZ", Size = "StarScale", Objects = p => new[] { p.Heart } },
            new DragTarget { Tab = "Trash", X = "StarX", Z = "StarZ", Size = "TrashScale", Objects = p => new[] { p.Trash } }, // the star's spot
            new DragTarget { Tab = "Share", X = "ShareX", Z = "ShareZ", Size = "ShareScale", Objects = p => new[] { p.Share } },
            new DragTarget { Tab = "Hand", X = "HandX", Z = "HandZ", Size = "HandScale", Objects = p => new[] { p.Hand?.Icon } },
        };

        /// <summary>A text: with a plate, the corner sizes the plate; without, the text.</summary>
        private static DragTarget TextTarget(string name, Func<RowParts, RowText> text) => new DragTarget
        {
            Tab = name, X = name + "X", Z = name + "Z", Size = name + "Scale", W = name + "PlateW", H = name + "PlateH",
            Objects = p => { var t = text(p); return t == null ? new Transform[0] : new[] { t.T, t.Plate != null ? t.Plate.transform : null }; },
        };

        private static bool HasPlate(DragTarget d) =>
            d.Size != null && d.W != null &&
            Field(d.Tab + "Plate") is float plate && Mathf.RoundToInt(plate) > 0;

        private enum DragMode { None, Move, Resize }

        private static DragTarget _selected;
        private static RowParts _selectedRow;
        private static DragMode _dragMode;
        private static Vector3 _dragStart; // mouse, row space
        private static Vector3 _dragPivot; // the element's own position, row space (uniform resize)
        private static float _startX, _startZ, _startSize, _startW, _startH;
        private static Texture2D _outline, _outlineFaint, _handle;
        private const float HandlePx = 14f;

        /// <summary>Per frame while the editor is open: pick, move, resize.</summary>
        internal static void DragUpdate()
        {
            if (!ReplaysEditor.Visible || !PageShown) { _dragMode = DragMode.None; return; }
            var mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && !PanelInputBlock.OverEditorPanel())
            {
                if (_selected != null && _selectedRow != null && OverHandle(_selectedRow, _selected, mouse))
                    BeginDrag(DragMode.Resize, _selectedRow, _selected);
                else
                {
                    var hit = HitTest(mouse, out var row);
                    if (hit != null)
                    {
                        _selected = hit;
                        _selectedRow = row;
                        ReplaysEditor.SelectTab(hit.Tab);
                        BeginDrag(DragMode.Move, row, hit);
                    }
                }
            }
            if (_dragMode != DragMode.None && Input.GetMouseButton(0))
            {
                if (_selectedRow?.Root != null && RowPoint(_selectedRow, mouse, out var at)) Drag(at - _dragStart, at);
            }
            else _dragMode = DragMode.None;
        }

        /// <summary>A press here would grab an element (the page must not get it).</summary>
        internal static bool DragWouldGrab()
        {
            if (!ReplaysEditor.Visible || !PageShown) return false;
            var mouse = Input.mousePosition;
            if (_selected != null && _selectedRow != null && OverHandle(_selectedRow, _selected, mouse)) return true;
            return HitTest(mouse, out _) != null;
        }

        private static void BeginDrag(DragMode mode, RowParts row, DragTarget d)
        {
            if (!RowPoint(row, Input.mousePosition, out _dragStart)) return;
            _dragMode = mode;
            _startX = (float)Field(d.X);
            _startZ = (float)Field(d.Z);
            _startSize = d.Size != null ? (float)Field(d.Size) : 0f;
            _startW = d.W != null ? (float)Field(d.W) : 0f;
            _startH = d.H != null ? (float)Field(d.H) : 0f;
            var first = FirstObject(row, d);
            _dragPivot = first != null ? row.Root.InverseTransformPoint(first.position) : _dragStart;
        }

        private static void Drag(Vector3 delta, Vector3 at)
        {
            var d = _selected;
            if (_dragMode == DragMode.Move)
            {
                SetField(d.X, Snap(_startX + delta.x));
                SetField(d.Z, Snap(_startZ + delta.z));
            }
            else if (d.W != null && (d.Size == null || HasPlate(d)))
            {
                // Box: the corner follows the mouse, the opposite corner stays (X/Z are the box centre).
                float w = Mathf.Max(1f, _startW + delta.x), h = Mathf.Max(1f, _startH + delta.z);
                SetField(d.W, Snap(w));
                SetField(d.H, Snap(h));
                SetField(d.X, Snap(_startX + (w - _startW) / 2f));
                SetField(d.Z, Snap(_startZ + (h - _startH) / 2f));
            }
            else if (d.Size != null)
            {
                Vector2 from = new Vector2(_dragStart.x - _dragPivot.x, _dragStart.z - _dragPivot.z);
                Vector2 to = new Vector2(at.x - _dragPivot.x, at.z - _dragPivot.z);
                if (from.magnitude < 0.5f) return;
                SetField(d.Size, Mathf.Clamp(Mathf.Round(_startSize * to.magnitude / from.magnitude * 100f) / 100f, 0.05f, 20f));
            }
            ApplyLayout();
        }

        private static float Snap(float v) => Mathf.Round(v * 20f) / 20f;

        private static object Field(string name) => typeof(Layout).GetField(name)?.GetValue(Layout.Current);

        private static void SetField(string name, float v) => typeof(Layout).GetField(name)?.SetValue(Layout.Current, v);

        /// <summary>The smallest element under the mouse on any row (the background only when nothing else is).</summary>
        private static DragTarget HitTest(Vector3 mouse, out RowParts row)
        {
            row = null;
            DragTarget best = null;
            float bestArea = float.MaxValue;
            foreach (var p in Parts)
            {
                if (p.Root == null || !RowPoint(p, mouse, out var at)) continue;
                foreach (var d in DragTargets)
                {
                    if (!RowBox(p, d, out var box)) continue;
                    if (!box.Contains(new Vector2(at.x, at.z))) continue;
                    float area = box.width * box.height * (d.Tab == "Background" ? 1000f : 1f);
                    if (area < bestArea) { bestArea = area; best = d; row = p; }
                }
            }
            return best;
        }

        private static Transform FirstObject(RowParts p, DragTarget d)
        {
            foreach (var t in d.Objects(p))
                if (t != null && t.gameObject.activeInHierarchy) return t;
            return null;
        }

        /// <summary>The element's box in row space (x, z), from its visible renderers.</summary>
        private static bool RowBox(RowParts p, DragTarget d, out Rect box)
        {
            box = default;
            bool any = false;
            float x0 = 0, z0 = 0, x1 = 0, z1 = 0;
            foreach (var t in d.Objects(p))
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled) continue;
                    var b = r.bounds;
                    if (b.size.sqrMagnitude < 1e-6f) continue;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var l = p.Root.InverseTransformPoint(c);
                        if (!any) { x0 = x1 = l.x; z0 = z1 = l.z; any = true; }
                        else { x0 = Mathf.Min(x0, l.x); x1 = Mathf.Max(x1, l.x); z0 = Mathf.Min(z0, l.z); z1 = Mathf.Max(z1, l.z); }
                    }
                }
            }
            if (!any) return false;
            box = Rect.MinMaxRect(x0, z0, x1, z1);
            return true;
        }

        private static Camera PageCamera(RowParts p) => CameraUtils.FindFirstByLayer(p.Root.gameObject.layer);

        /// <summary>The mouse on the row's plane, in row space.</summary>
        private static bool RowPoint(RowParts p, Vector3 mouse, out Vector3 at)
        {
            at = default;
            var cam = PageCamera(p);
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(mouse);
            var plane = new Plane(p.Root.up, p.Root.position);
            if (!plane.Raycast(ray, out float dist)) return false;
            at = p.Root.InverseTransformPoint(ray.GetPoint(dist));
            return true;
        }

        /// <summary>The element's outline on screen, in GUI coordinates (pixels, y down).</summary>
        private static bool ScreenBox(RowParts p, DragTarget d, out Rect rect)
        {
            rect = default;
            var cam = PageCamera(p);
            if (cam == null || !RowBox(p, d, out var box)) return false;
            float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
            foreach (var c in new[] { new Vector2(box.xMin, box.yMin), new Vector2(box.xMax, box.yMin),
                                      new Vector2(box.xMin, box.yMax), new Vector2(box.xMax, box.yMax) })
            {
                var s = cam.WorldToScreenPoint(p.Root.TransformPoint(new Vector3(c.x, 0f, c.y)));
                float gy = Screen.height - s.y;
                sx0 = Mathf.Min(sx0, s.x); sx1 = Mathf.Max(sx1, s.x);
                sy0 = Mathf.Min(sy0, gy); sy1 = Mathf.Max(sy1, gy);
            }
            rect = Rect.MinMaxRect(sx0, sy0, sx1, sy1);
            return true;
        }

        private static bool OverHandle(RowParts p, DragTarget d, Vector3 mouse)
        {
            if (!ScreenBox(p, d, out var r)) return false;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            return Vector2.Distance(gui, new Vector2(r.xMax, r.yMin)) <= HandlePx;
        }

        /// <summary>Outline of the hovered and the selected element, the resize handle (screen pixels; called from
        /// the editor's OnGUI before it scales its own panel).</summary>
        internal static void DrawDragOverlay()
        {
            if (Event.current.type != EventType.Repaint || !PageShown) return;
            if (_outline == null)
            {
                _outline = Solid(new Color(1f, 0.83f, 0.42f, 1f));
                _outlineFaint = Solid(new Color(1f, 0.95f, 0.8f, 0.55f));
                _handle = Solid(new Color(1f, 0.83f, 0.42f, 1f));
            }
            var mouse = Input.mousePosition;
            if (_dragMode == DragMode.None && !PanelInputBlock.OverEditorPanel())
            {
                var hover = HitTest(mouse, out var row);
                if (hover != null && (hover != _selected || row != _selectedRow) && ScreenBox(row, hover, out var hr))
                    Frame(hr, _outlineFaint, 1f);
            }
            if (_selected != null && _selectedRow?.Root != null && ScreenBox(_selectedRow, _selected, out var r))
            {
                Frame(r, _outline, 2f);
                GUI.DrawTexture(new Rect(r.xMax - HandlePx / 2f, r.yMin - HandlePx / 2f, HandlePx, HandlePx), _handle);
            }
        }

        private static void Frame(Rect r, Texture2D t, float w)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), t);
            GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), t);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), t);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), t);
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
