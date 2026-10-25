using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The plugin's drawn symbols (the game has no bug, trash bin or share icon of its own), drawn like the replay
    /// bar's glyphs: gold, over a dark copy offset down. Shapes are convex polygons in a box <c>size</c> wide around
    /// <c>centre</c>, y down; <see cref="Build"/> lays them in a unit square of the page's plane (x right, z up the
    /// screen, y towards the camera).
    /// </summary>
    internal static class Glyph
    {
        public static readonly Color Idle = new Color(0.85f, 0.85f, 0.85f, 1f);

        /// <summary>A glyph's colours: its face from top to bottom, the copy offset under it (none: alpha 0), its
        /// details, and an outline round it (none: alpha 0).</summary>
        public struct Palette
        {
            public Color32 Top, Bottom, Shadow, Detail, Outline;
        }

        private const float OutlineWidth = 0.05f; // of the glyph's box

        /// <summary>The bar's and the bug button's: gold over a dark copy.</summary>
        public static readonly Palette GoldPalette = new Palette
        {
            Top = new Color32(255, 214, 110, 255), Bottom = new Color32(196, 140, 40, 255),
            Shadow = new Color32(0, 0, 0, 200), Detail = new Color32(0, 0, 0, 200),
        };

        /// <summary>The row buttons in the place's tab, as the unsaved star beside them: grey in a black outline
        /// (2026-10-25: plain grey and plain near black were tried; the owner chose this). Hovered, each takes its own
        /// colour: the trash bin red, the share arrow blue (the star turns gold).</summary>
        public static readonly Palette GreyPalette = new Palette
        {
            Top = new Color32(168, 168, 168, 255), Bottom = new Color32(112, 112, 112, 255),
            Detail = new Color32(40, 40, 40, 255), Outline = new Color32(16, 14, 12, 255),
        };

        public static readonly Palette RedPalette = new Palette
        {
            Top = new Color32(240, 96, 72, 255), Bottom = new Color32(176, 36, 24, 255),
            Detail = new Color32(70, 12, 8, 255), Outline = new Color32(16, 14, 12, 255),
        };

        public static readonly Palette BluePalette = new Palette
        {
            Top = new Color32(120, 190, 255, 255), Bottom = new Color32(40, 112, 214, 255),
            Detail = new Color32(10, 30, 70, 255), Outline = new Color32(16, 14, 12, 255),
        };

        /// <summary>A trash bin: handle, lid, a body narrowing downwards; its three slits are <see cref="TrashSlits"/>.</summary>
        public static IEnumerable<Vector2[]> Trash(Vector2 centre, float size)
        {
            Vector2 P(float x, float y) => centre + new Vector2(x, y) * size;
            yield return Box(P(-0.12f, -0.45f), P(0.12f, -0.36f)); // handle
            yield return Box(P(-0.37f, -0.37f), P(0.37f, -0.27f)); // lid
            yield return new[] { P(-0.30f, -0.21f), P(0.30f, -0.21f), P(0.24f, 0.45f), P(-0.24f, 0.45f) }; // body
        }

        public static IEnumerable<Vector2[]> TrashSlits(Vector2 centre, float size)
        {
            Vector2 P(float x, float y) => centre + new Vector2(x, y) * size;
            foreach (float x in new[] { -0.12f, 0f, 0.12f })
                yield return Segment(P(x, -0.11f), P(x * 0.85f, 0.35f), 0.05f * size);
        }

        /// <summary>A forward arrow (as chat apps' Forward): a head pointing right at the top, its tail curving down to
        /// the bottom left.</summary>
        public static IEnumerable<Vector2[]> Share(Vector2 centre, float size)
        {
            Vector2 P(float x, float y) => centre + new Vector2(x, y) * size;
            yield return new[] { P(0.06f, -0.44f), P(0.47f, -0.10f), P(0.06f, 0.24f) }; // head
            // The tail: a quadratic curve from the head's base to the bottom left, as overlapping quads and joints.
            Vector2 a = P(0.10f, -0.10f), c = P(-0.36f, -0.10f), b = P(-0.40f, 0.42f);
            const int n = 8;
            float w = 0.20f * size;
            Vector2 Q(float t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = Q(i / (float)n), p1 = Q((i + 1) / (float)n);
                yield return Segment(p0, p1, w);
                yield return Ellipse(p1, w / 2f, w / 2f, 10);
            }
        }

        public static Vector2[] Ellipse(Vector2 c, float rx, float ry, int n)
        {
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * (float)System.Math.PI * 2f / n;
                pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
            }
            return pts;
        }

        public static Vector2[] Segment(Vector2 a, Vector2 b, float width)
        {
            var n = new Vector2(a.y - b.y, b.x - a.x).normalized * (width / 2f);
            return new[] { a + n, b + n, b - n, a - n };
        }

        private static Vector2[] Box(Vector2 min, Vector2 max) =>
            new[] { min, new Vector2(max.x, min.y), max, new Vector2(min.x, max.y) };

        /// <summary>The glyph in a unit square: the dark copy first, the gold over it, then the dark details. The gold
        /// darkens downwards over each shape, or (wholeGradient) over the whole glyph: a shape drawn as many pieces
        /// (the share arrow's tail) would otherwise show bands.</summary>
        public static Mesh Build(string name, IEnumerable<Vector2[]> shapes, IEnumerable<Vector2[]> details = null,
                                 bool wholeGradient = false, Palette? palette = null)
        {
            var pal = palette ?? GoldPalette;
            var v = new List<Vector3>();
            var c = new List<Color32>();
            var t = new List<int>();
            var list = new List<Vector2[]>(shapes);
            float g0 = float.MaxValue, g1 = float.MinValue;
            foreach (var s in list)
                foreach (var p in s) { g0 = Mathf.Min(g0, p.y); g1 = Mathf.Max(g1, p.y); }
            void Add(Vector2[] pts, Vector2 offset, Color32 top, Color32 bottom)
            {
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var p in pts) { y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y); }
                if (wholeGradient) { y0 = g0; y1 = g1; }
                int i = v.Count;
                foreach (var p in pts)
                {
                    v.Add(new Vector3(p.x + offset.x, 0f, -(p.y + offset.y)));
                    c.Add(Mix(top, bottom, (p.y - y0) / Mathf.Max(1e-4f, y1 - y0)));
                }
                for (int k = 1; k + 1 < pts.Length; k++) { t.Add(i); t.Add(i + k); t.Add(i + k + 1); }
            }
            if (pal.Shadow.a > 0)
                foreach (var s in list) Add(s, new Vector2(0.03f, 0.045f), pal.Shadow, pal.Shadow);
            // The outline: the shapes drawn under the face, moved a little each way round (eight directions).
            if (pal.Outline.a > 0)
                for (int k = 0; k < 8; k++)
                {
                    float a = k * (float)System.Math.PI / 4f;
                    var o = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * OutlineWidth;
                    foreach (var s in list) Add(s, o, pal.Outline, pal.Outline);
                }
            foreach (var s in list) Add(s, Vector2.zero, pal.Top, pal.Bottom);
            if (details != null)
                foreach (var s in details) Add(s, Vector2.zero, pal.Detail, pal.Detail);
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetUVs(0, new List<Vector2>(new Vector2[v.Count]));
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Color32 Mix(Color32 a, Color32 b, float t) => new Color32(
            (byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));
    }
}
