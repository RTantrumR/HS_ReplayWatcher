using System;
using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The views' tabs in the page's title: three stone plates (Recent, Saved, Imported) on one wooden panel that
    /// hangs from the frame's top bar, slanted only at its two ends. All of it is cut from the frame's own texture
    /// (Bacon_StatsFrame), where the single title plate and its wooden notch are painted: the frame's mesh can't be
    /// changed, so the panel is laid over the painted notch and covers it.
    ///
    /// The frame's mesh shows its texture evenly over the whole bar (measured 2026-10-28 against the untouched
    /// frame: 1.43 across by 1.45 down, screen px per texture px at 1080p), so the panel is laid out in the
    /// texture's own rows around the painted plate's middle (y down): the bar from just inside its top highlight
    /// (18) to the notch's top (40), as a patch mirrored from beside the plate, which covers the painted plate's
    /// top and carries the bar's own bottom edge on; the panel's face in the bar's grain; and the notch's wood edge
    /// (77..87) at the panel's own bottom. The plates sit as painted, scaled together by TabScale about the painted
    /// plate's middle, and the texture's two slanted ends run the panel's height. The face's bands are clipped to
    /// the slants.
    ///
    /// The panel carries no shadow of its own, by the owner's call (2026-10-28): a dark band the width of the bar
    /// reads as a plate laid over the page. It ends instead where the painted notch's own shadow does (96), so its
    /// wood covers that shadow — which the frame draws under the painted plate whatever we do — and meets the
    /// page's torn top edge, which the notch's shadow hid anyway.
    /// Laying the bands out in hand-measured screen pixels instead (as to 2026-10-27) squashed the panel by a
    /// sixth: the plates' bolts came out oval, the bar's bottom edge was lost under the patch, a sliver of the
    /// painted plate showed above it, and the dark underside reached a page-height too high.
    /// The title text is the middle tab's label; copies label the others. The shown view's label is in the title's
    /// colour, the others faded (darkened again on hover).
    /// </summary>
    internal static partial class ReplaysUi
    {
        // In the 1024 px texture (y down): the plate (an octagon, its middle at 509, 49) and the notch's slanted edges.
        private static readonly Vector2[] TabPlate =
        {
            new Vector2(385, 21), new Vector2(633, 21), new Vector2(651, 39), new Vector2(651, 59),
            new Vector2(641, 77), new Vector2(379, 77), new Vector2(367, 59), new Vector2(367, 39),
        };
        private const float TabPlateU = 509f, TabPlateV = 49f, TabPlateLeft = 367f, TabPlateRight = 651f;
        private static readonly Vector2[] TabLeftSlant =
            { new Vector2(349, 40), new Vector2(357, 40), new Vector2(383, 89), new Vector2(375, 89) };
        private static readonly Vector2[] TabRightSlant =
            { new Vector2(660, 40), new Vector2(668, 40), new Vector2(651, 89), new Vector2(643, 89) };
        private const float TabSlantTopV = 40f, TabSlantBottomV = 89f;
        // The panel's bands, in the texture's own rows (y down): the bar patch's top, just inside the bar's top
        // highlight (17..19), so the patch carries the highlight's tail on rather than cutting across the grain;
        // the panel's bottom, where the painted notch's own shadow ends; and the wood edge's ten rows above it.
        private const float TabBarTopV = 18f, TabBottomV = 96f, TabEdgeTopV = TabBottomV - 10f;
        // Band sources in the texture: the bar's grain beside the plate (clean wood, no bolt, edge or lip: measured
        // 2026-10-28 over the whole texture, which holds no taller one), and the notch's wood edge.
        private static readonly Rect TabFaceSrc = new Rect(112, 20, 250, 18), TabEdgeSrc = new Rect(400, 77, 220, 10);
        private const float TabPaintedHalfW = 210f; // 203 measured, from a middle the title only roughly marks

        private static readonly string[] TabNames = { "Recent", "Saved", "Imported" };
        private static Transform _tabFrame, _tabPanel;
        private static readonly UberText[] _tabTexts = new UberText[3];
        private static readonly bool[] _tabHover = new bool[3];
        private static readonly Transform[] _tabHits = new Transform[3];
        private static Color _tabActive;
        private static Vector3 _titleHome, _titleScale; // the title's own place and size (it labels the middle tab)
        private static bool _tabsBuilt, _tabsLogged;

        private static bool HasTabs => _tabsBuilt;

        /// <summary>The label the middle tab (the page's own title text) shows.</summary>
        private static string MiddleTabName => TabNames[1];

        /// <summary>The tabs, from the frame's texture; false when the frame isn't as expected (the arrows then).</summary>
        private static bool CreateTabs()
        {
            if (_tabsBuilt || _title == null) return _tabsBuilt;
            var frame = _title.transform.parent != null ? _title.transform.parent.Find("Frame") : null;
            var renderer = frame != null ? frame.GetComponent<MeshRenderer>() : null;
            var tex = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture : null;
            if (tex == null || tex.name != "Bacon_StatsFrame")
            {
                Plugin.Log.LogWarning($"Replays tabs: no page frame as expected (texture {tex?.name}); arrows instead");
                return false;
            }
            _tabFrame = frame;
            _tabActive = _title.TextColor;
            _titleHome = _title.transform.localPosition;
            _titleScale = _title.transform.localScale;
            var panel = new GameObject("ReplayTabs");
            panel.layer = frame.gameObject.layer;
            _tabPanel = panel.transform;
            _tabPanel.SetParent(frame, false);
            panel.AddComponent<MeshFilter>();
            panel.AddComponent<MeshRenderer>().sharedMaterial = renderer.sharedMaterial;
            for (int i = 0; i < 3; i++)
            {
                int view = i;
                var hit = new GameObject("ReplayTab" + TabNames[i]) { layer = _title.gameObject.layer };
                hit.transform.SetParent(frame, false);
                hit.AddComponent<BoxCollider>();
                var element = hit.AddComponent<PegUIElement>();
                element.AddEventListener(UIEventType.RELEASE, e => ReplayLibrary.Show(ReplayLibrary.Views[view]));
                element.AddEventListener(UIEventType.ROLLOVER, e => { _tabHover[view] = true; ColourTabs(); });
                element.AddEventListener(UIEventType.ROLLOUT, e => { _tabHover[view] = false; ColourTabs(); });
                _tabHits[i] = hit.transform;
                _tabTexts[i] = i == 1 ? _title : CopyTitle("ReplayTabText" + TabNames[i], TabNames[i]);
            }
            _title.GameStringLookup = false;
            _title.Text = MiddleTabName;
            _tabsBuilt = true;
            PlaceTabs(Layout.Current);
            Plugin.Log.LogInfo($"Replays tabs built over the frame's plate ({tex.width}x{tex.height})");
            return true;
        }

        private static UberText CopyTitle(string name, string text)
        {
            var go = UnityEngine.Object.Instantiate(_title.gameObject, _title.transform.parent, false);
            go.name = name;
            foreach (Transform child in go.transform)
                if (child.name.StartsWith("UberText_RenderObject")) UnityEngine.Object.Destroy(child.gameObject);
            var t = go.GetComponent<UberText>();
            t.GameStringLookup = false;
            t.Text = text;
            return t;
        }

        /// <summary>A point of the panel: its place (1080p screen px from the painted plate's middle) and its texture
        /// pixel.</summary>
        private struct TabVert
        {
            public Vector2 At, Tex;
            public TabVert(Vector2 at, Vector2 tex) { At = at; Tex = tex; }
        }

        /// <summary>The part of a convex polygon on one side of a line (Sutherland-Hodgman; texture pixels along).
        /// keep(p) is positive on the kept side.</summary>
        private static List<TabVert> ClipTab(List<TabVert> poly, Func<Vector2, float> keep)
        {
            var result = new List<TabVert>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                float fa = keep(a.At), fb = keep(b.At);
                if (fa >= 0f) result.Add(a);
                if (fa * fb < 0f)
                {
                    float t = fa / (fa - fb);
                    result.Add(new TabVert(Vector2.Lerp(a.At, b.At, t), Vector2.Lerp(a.Tex, b.Tex, t)));
                }
            }
            return result;
        }

        /// <summary>The panel's mesh and the tabs' hit boxes (in the frame's units), and the labels (beside the title's
        /// own place), from the layout: TabPx frame units per 1080p screen px, TabPlateSX/SY the painted plate's
        /// screen px per texture px, TabScale the group's size (about the painted plate's middle), TabShift its move right and
        /// TabGap between the plates (screen px), TabX/TabZ the title's offset from the painted plate's middle (screen
        /// px, right and down), TabLift above the frame (frame units).</summary>
        private static void PlaceTabs(Layout l)
        {
            if (!_tabsBuilt || _tabFrame == null || _tabPanel == null) return;
            var tex = _tabPanel.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            float k = l.TabPx;
            // Screen right, screen up and towards the camera, in the frame's units (it may be turned in its plane).
            Vector3 right = _tabFrame.InverseTransformDirection(Vector3.right).normalized;
            Vector3 up = _tabFrame.InverseTransformDirection(Vector3.forward).normalized;
            Vector3 normal = _tabFrame.InverseTransformDirection(Vector3.up).normalized;
            var titleParent = _title.transform.parent;
            var anchor = _tabFrame.InverseTransformPoint(titleParent.TransformPoint(_titleHome));
            anchor -= normal * Vector3.Dot(anchor, normal); // into the frame's plane; lifted from its top below
            anchor -= right * (l.TabX * k) - up * (l.TabZ * k);
            // The frame's top towards the camera: its mesh's farthest corner that way.
            var b = _tabFrame.GetComponent<MeshFilter>().sharedMesh.bounds;
            float top = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                top = Mathf.Max(top, Vector3.Dot(corner, normal));
            }
            Vector3 At(Vector2 p, float lift) => anchor + right * (p.x * k) - up * (p.y * k) + normal * (top + lift);
            Vector2 Uv(Vector2 t) => new Vector2(t.x / tex.width, 1f - t.y / tex.height);

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            void Add(List<TabVert> poly, float lift)
            {
                if (poly.Count < 3) return;
                int start = verts.Count;
                foreach (var v in poly) { verts.Add(At(v.At, lift)); uvs.Add(Uv(v.Tex)); }
                for (int i = 1; i + 1 < poly.Count; i++) { tris.Add(start); tris.Add(start + i); tris.Add(start + i + 1); }
            }

            // Where a texture row sits (screen px from the painted plate's middle, y down), at the frame's own
            // scale: every band then lands on the row the frame itself draws there.
            float fy = l.TabPlateSY;
            float Y(float v) => (v - TabPlateV) * fy;
            float barTop = Y(TabBarTopV), faceTop = Y(TabSlantTopV), edgeTop = Y(TabEdgeTopV), bottom = Y(TabBottomV);
            // The plates: as painted (sx, sy screen px per texture px), scaled by s about the painted plate's middle.
            float s = l.TabScale, sx = l.TabPlateSX * s, sy = fy * s;
            float plateW = (TabPlateRight - TabPlateLeft) * sx, pitch = plateW + l.TabGap * s;
            Vector2 PlateAt(Vector2 t, int slot) =>
                new Vector2(l.TabShift + slot * pitch + (t.x - TabPlateU) * sx, (t.y - TabPlateV) * sy);
            // The slanted ends: the texture's edges, kept at the outer plates' sides as painted, run the panel's own
            // height (a touch longer than the notch's, which ends where its shadow began).
            float slantK = (bottom - faceTop) / (TabSlantBottomV - TabSlantTopV);
            float leftEdge = PlateAt(new Vector2(TabPlateLeft, 0f), -1).x, rightEdge = PlateAt(new Vector2(TabPlateRight, 0f), 1).x;
            Vector2 SlantAt(Vector2 t, bool left) =>
                new Vector2((left ? leftEdge : rightEdge) + (t.x - (left ? TabPlateLeft : TabPlateRight)) * sx,
                            faceTop + (t.y - TabSlantTopV) * slantK);
            // The lines the face's bands keep inside: each slant's middle (straight up above its top).
            float Down(float y) => Mathf.Max(0f, (y - faceTop) / (bottom - faceTop));
            Vector2 LMid(float y) => Vector2.LerpUnclamped(Vector2.Lerp(SlantAt(TabLeftSlant[0], true), SlantAt(TabLeftSlant[1], true), 0.5f),
                                                           Vector2.Lerp(SlantAt(TabLeftSlant[3], true), SlantAt(TabLeftSlant[2], true), 0.5f),
                                                           Down(y));
            Vector2 RMid(float y) => Vector2.LerpUnclamped(Vector2.Lerp(SlantAt(TabRightSlant[0], false), SlantAt(TabRightSlant[1], false), 0.5f),
                                                           Vector2.Lerp(SlantAt(TabRightSlant[3], false), SlantAt(TabRightSlant[2], false), 0.5f),
                                                           Down(y));
            // A line's x at a height (straight up above the slants' top): bands are split there, so each clip is
            // against one straight line.
            float XOn(Func<float, Vector2> line, float y)
            {
                Vector2 a = line(faceTop), c = line(bottom);
                return y <= faceTop ? a.x : Mathf.LerpUnclamped(a.x, c.x, (y - a.y) / (c.y - a.y));
            }
            // A band across the panel, tiled from a source strip at the texture's own scale across (sx), rows vTop
            // to vBottom: every other tile is mirrored, so the grain meets itself at each join, and the tiles sit on
            // one grid whatever the band, so bands stacked down the panel line up. The ends go to the slants, where
            // the clipping takes the tiles that fall outside.
            void Band(float y0, float y1, Rect src, float vTop, float vBottom, float lift)
            {
                float x1 = Mathf.Max(RMid(y0).x, RMid(y1).x) + 2f;
                float tileW = src.width * sx;
                float mid = (y0 + y1) / 2f; // a band is all above or all below the slants' top
                int j = Mathf.FloorToInt((Mathf.Min(LMid(y0).x, LMid(y1).x) - 2f - l.TabShift) / tileW);
                for (float x = l.TabShift + j * tileW; x < x1; x += tileW, j++)
                {
                    float uA = (j & 1) == 0 ? src.xMin : src.xMax, uB = (j & 1) == 0 ? src.xMax : src.xMin;
                    var poly = new List<TabVert>
                    {
                        new TabVert(new Vector2(x, y0), new Vector2(uA, vTop)),
                        new TabVert(new Vector2(x + tileW, y0), new Vector2(uB, vTop)),
                        new TabVert(new Vector2(x + tileW, y1), new Vector2(uB, vBottom)),
                        new TabVert(new Vector2(x, y1), new Vector2(uA, vBottom)),
                    };
                    poly = ClipTab(poly, p => p.x - XOn(LMid, mid <= faceTop ? faceTop : p.y));
                    poly = ClipTab(poly, p => XOn(RMid, mid <= faceTop ? faceTop : p.y) - p.x);
                    Add(poly, lift);
                }
            }
            // The face: the strip laid down the panel at the texture's own scale too, every other one flipped so the
            // grain meets itself there as well. One strip stretched over the whole face came out smeared, the grain
            // washed out of it (the owner, 2026-10-28); the texture holds no wood band tall enough to fill it whole.
            void Face(float y0, float y1, Rect src, float lift)
            {
                float tileH = src.height * fy;
                for (int k = 0; y0 + k * tileH < y1 - 0.5f; k++)
                {
                    float a = y0 + k * tileH, b = Mathf.Min(y1, a + tileH), rows = (b - a) / fy;
                    bool flip = (k & 1) != 0;
                    Band(a, b, src, flip ? src.yMax : src.yMin, flip ? src.yMax - rows : src.yMin + rows, lift);
                }
            }
            List<TabVert> Piece(Vector2[] pts, Func<Vector2, Vector2> place)
            {
                var poly = new List<TabVert>();
                foreach (var p in pts) poly.Add(new TabVert(place(p), p));
                return poly;
            }

            float lift = l.TabLift;
            // Over the painted plate's top (in the bar): the bar itself, mirrored from just beside it, so its grain runs
            // on at the outer edges. This space's origin is the painted plate's middle; the bar maps across as the
            // plate does (unscaled).
            float barSx = l.TabPlateSX;
            void BarPatch(float x0, float x1, float mirrorAt)
            {
                if (x1 <= x0) return;
                float U(float x) => 2f * (TabPlateU + mirrorAt / barSx) - (TabPlateU + x / barSx);
                Add(new List<TabVert>
                {
                    new TabVert(new Vector2(x0, barTop), new Vector2(U(x0), TabBarTopV)),
                    new TabVert(new Vector2(x1, barTop), new Vector2(U(x1), TabBarTopV)),
                    new TabVert(new Vector2(x1, faceTop), new Vector2(U(x1), TabSlantTopV)),
                    new TabVert(new Vector2(x0, faceTop), new Vector2(U(x0), TabSlantTopV)),
                }, lift);
            }
            // The whole painted top, in two halves (the plate's cut corners leave its corners showing otherwise); they
            // meet in the middle, under the middle plate.
            BarPatch(-TabPaintedHalfW, 0f, -TabPaintedHalfW);
            BarPatch(0f, TabPaintedHalfW, TabPaintedHalfW);
            Face(faceTop, edgeTop, TabFaceSrc, lift);
            Band(edgeTop, bottom, TabEdgeSrc, TabEdgeSrc.yMin, TabEdgeSrc.yMax, lift);
            Add(Piece(TabLeftSlant, p => SlantAt(p, true)), lift * 1.5f);
            Add(Piece(TabRightSlant, p => SlantAt(p, false)), lift * 1.5f);
            for (int i = -1; i <= 1; i++)
            {
                int slot = i;
                Add(Piece(TabPlate, p => PlateAt(p, slot)), lift * 2f);
            }

            // Both windings: the frame may be turned, which flips which side faces the camera (culled otherwise).
            for (int i = 0, n = tris.Count; i < n; i += 3) { tris.Add(tris[i]); tris.Add(tris[i + 2]); tris.Add(tris[i + 1]); }
            var mf = _tabPanel.GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh ?? new Mesh { name = "ReplayTabs" };
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            if (!_tabsLogged)
            {
                _tabsLogged = true;
                Plugin.Log.LogInfo($"Replays tabs: panel {_tabPanel.GetComponent<MeshRenderer>().bounds}, title {_title.transform.position}");
            }

            // Hit boxes over the plates; each label where the title's spot on its plate is (the title itself on the
            // middle one), moved from the title's own place by the plate's offset from the painted one.
            for (int i = 0; i < 3; i++)
            {
                var mid = PlateAt(new Vector2(TabPlateU, TabPlateV), i - 1);
                _tabHits[i].localPosition = At(mid, lift * 2f);
                _tabHits[i].localRotation = Quaternion.identity;
                _tabHits[i].localScale = Vector3.one;
                var span = right * (plateW * k) + up * ((TabPlateBottomV - TabPlateTopV) * sy * k) + normal * 0.1f;
                _tabHits[i].GetComponent<BoxCollider>().size = new Vector3(Mathf.Abs(span.x), Mathf.Abs(span.y), Mathf.Abs(span.z));
                var offset = titleParent.InverseTransformVector(_tabFrame.TransformVector(right * (mid.x * k) - up * (mid.y * k)));
                var t = _tabTexts[i].transform;
                t.localPosition = _titleHome + offset;
                t.localRotation = _title.transform.localRotation;
                t.localScale = _titleScale * s;
            }
            ColourTabs();
        }

        private const float TabPlateTopV = 21f, TabPlateBottomV = 77f;

        /// <summary>Per frame: the panel on the frame's layer. The page moves its frame to another layer while parked
        /// off screen (28, seen 2026-10-27) and back when shown; a panel left on the parked one isn't drawn.</summary>
        private static void SyncTabLayers()
        {
            if (!_tabsBuilt || _tabPanel == null || _tabFrame == null) return;
            int layer = _tabFrame.gameObject.layer;
            if (_tabPanel.gameObject.layer != layer) _tabPanel.gameObject.layer = layer;
            foreach (var hit in _tabHits)
                if (hit != null && hit.gameObject.layer != _title.gameObject.layer) hit.gameObject.layer = _title.gameObject.layer;
        }

        /// <summary>The shown view's label in the title's colour, the others faded unless hovered.</summary>
        private static void ColourTabs()
        {
            if (!_tabsBuilt) return;
            var idle = ParseColor(Layout.Current.TabIdleColor, Color.Lerp(_tabActive, new Color(0.62f, 0.52f, 0.40f), 0.6f));
            for (int i = 0; i < 3; i++)
            {
                if (_tabTexts[i] == null) continue;
                bool on = ReplayLibrary.Current == ReplayLibrary.Views[i] || _tabHover[i];
                _tabTexts[i].TextColor = on ? _tabActive : idle;
            }
        }
    }
}
