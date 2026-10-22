using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The bug button's symbol, drawn like the replay bar's other glyphs (gold, over a dark copy offset down): a beetle
    /// seen from above, head, body with the wings' split, six legs and two antennae. The game has no bug icon of its
    /// own. Shapes are convex polygons in a box <c>size</c> wide around <c>centre</c>, y down.
    /// </summary>
    internal static class BugGlyph
    {
        public static IEnumerable<Vector2[]> Shapes(Vector2 centre, float size)
        {
            Vector2 P(float x, float y) => centre + new Vector2(x, y) * size;
            yield return Ellipse(P(0f, -0.31f), 0.11f * size, 0.10f * size, 14); // head
            yield return Ellipse(P(0f, 0.08f), 0.21f * size, 0.28f * size, 24); // body
            foreach (float side in new[] { -1f, 1f })
            {
                yield return Segment(P(side * 0.05f, -0.38f), P(side * 0.17f, -0.50f), 0.06f * size); // antenna
                yield return Segment(P(side * 0.14f, -0.08f), P(side * 0.38f, -0.24f), 0.075f * size); // front leg
                yield return Segment(P(side * 0.17f, 0.07f), P(side * 0.43f, 0.05f), 0.075f * size); // middle leg
                yield return Segment(P(side * 0.14f, 0.24f), P(side * 0.36f, 0.44f), 0.075f * size); // back leg
            }
        }

        /// <summary>The line between the wings, drawn dark over the body.</summary>
        public static Vector2[] Split(Vector2 centre, float size) =>
            Segment(centre + new Vector2(0f, -0.14f) * size, centre + new Vector2(0f, 0.34f) * size, 0.045f * size);

        private static Vector2[] Ellipse(Vector2 c, float rx, float ry, int n)
        {
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * (float)System.Math.PI * 2f / n;
                pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
            }
            return pts;
        }

        private static Vector2[] Segment(Vector2 a, Vector2 b, float width)
        {
            var n = new Vector2(a.y - b.y, b.x - a.x).normalized * (width / 2f);
            return new[] { a + n, b + n, b - n, a - n };
        }
    }

    internal static partial class ReplaysUi
    {
        private static Transform _bug, _backButton;
        private static Material _bugMaterial;
        private static bool _bugLogged;
        private static readonly Color BugIdle = new Color(0.85f, 0.85f, 0.85f, 1f);
        private static readonly Color32 BugGold = new Color32(255, 214, 110, 255), BugGoldDark = new Color32(196, 140, 40, 255),
                                         BugShadow = new Color32(0, 0, 0, 200);

        /// <summary>The page's bug button: the bug glyph as a flat mesh beside the Back button, clicked through the
        /// game's own input (a PegUIElement on its collider).</summary>
        private static void CreateBugButton(Transform back)
        {
            var shader = Shader.Find("Sprites/Default");
            if (back == null || shader == null || _bug != null) return;
            _backButton = back;
            var go = new GameObject("BugButton") { layer = back.gameObject.layer };
            _bug = go.transform;
            _bug.SetParent(back.parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = GlyphMesh();
            _bugMaterial = new Material(shader) { mainTexture = Texture2D.whiteTexture, color = BugIdle, name = "BugButton" };
            go.AddComponent<MeshRenderer>().sharedMaterial = _bugMaterial;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 1f);
            var element = go.AddComponent<PegUIElement>();
            element.AddEventListener(UIEventType.RELEASE, e => BugReport.Start());
            element.AddEventListener(UIEventType.ROLLOVER, e => _bugMaterial.color = Color.white);
            element.AddEventListener(UIEventType.ROLLOUT, e => _bugMaterial.color = BugIdle);
            PlaceBugButton(Layout.Current);
        }

        /// <summary>The glyph in a unit square of the page's plane (x right, z up the screen, y towards the camera):
        /// the dark copy first, the gold over it, the wings' split last.</summary>
        private static Mesh GlyphMesh()
        {
            var v = new List<Vector3>();
            var c = new List<Color32>();
            var t = new List<int>();
            void Add(Vector2[] pts, Vector2 offset, Color32 top, Color32 bottom)
            {
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var p in pts) { y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y); }
                int i = v.Count;
                foreach (var p in pts)
                {
                    v.Add(new Vector3(p.x + offset.x, 0f, -(p.y + offset.y)));
                    c.Add(Mix(top, bottom, (p.y - y0) / Mathf.Max(1e-4f, y1 - y0)));
                }
                for (int k = 1; k + 1 < pts.Length; k++) { t.Add(i); t.Add(i + k); t.Add(i + k + 1); }
            }
            var shapes = new List<Vector2[]>(BugGlyph.Shapes(Vector2.zero, 1f));
            foreach (var s in shapes) Add(s, new Vector2(0.03f, 0.045f), BugShadow, BugShadow);
            foreach (var s in shapes) Add(s, Vector2.zero, BugGold, BugGoldDark);
            Add(BugGlyph.Split(Vector2.zero, 1f), Vector2.zero, BugShadow, BugShadow);
            var mesh = new Mesh { name = "BugButton" };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetUVs(0, new List<Vector2>(new Vector2[v.Count]));
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Color32 Mix(Color32 a, Color32 b, float t) => new Color32(
            (byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));

        /// <summary>Left of the Back button, in screen terms (the page's frame is turned 180° in its plane, so the
        /// button's parent axes can't be used): BugX Back-button heights of gap from its left edge, BugZ of them up,
        /// BugScale of them tall. The page's plane is the world's XZ (y towards the camera).</summary>
        private static void PlaceBugButton(Layout l)
        {
            if (_bug == null || _backButton == null) return;
            Bounds? found = null;
            foreach (var r in _backButton.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (found == null) found = r.bounds;
                else { var e = found.Value; e.Encapsulate(r.bounds); found = e; }
            }
            if (found == null) return;
            var b = found.Value;
            float h = b.size.z;
            if (h < 1e-4f) return;
            float size = l.BugScale * h;
            _bug.position = new Vector3(b.min.x - l.BugX * h - size / 2f, b.max.y + 0.05f * h, b.center.z + l.BugZ * h);
            _bug.rotation = Quaternion.identity;
            var parentScale = _bug.parent.lossyScale;
            _bug.localScale = new Vector3(size / Mathf.Abs(parentScale.x), size / Mathf.Abs(parentScale.y), size / Mathf.Abs(parentScale.z));
            if (!_bugLogged)
            {
                _bugLogged = true;
                Plugin.Log.LogInfo($"Bug button placed at {_bug.position} ({size:0.00} wide) beside Back {b.center} (size {b.size})");
            }
        }
    }
}
