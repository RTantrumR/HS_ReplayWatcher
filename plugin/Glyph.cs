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
        private static readonly Color32 Gold = new Color32(255, 214, 110, 255), GoldDark = new Color32(196, 140, 40, 255),
                                        Shadow = new Color32(0, 0, 0, 200);

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

        /// <summary>The glyph in a unit square: the dark copy first, the gold over it, then the dark details.</summary>
        public static Mesh Build(string name, IEnumerable<Vector2[]> shapes, IEnumerable<Vector2[]> details = null)
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
            var list = new List<Vector2[]>(shapes);
            foreach (var s in list) Add(s, new Vector2(0.03f, 0.045f), Shadow, Shadow);
            foreach (var s in list) Add(s, Vector2.zero, Gold, GoldDark);
            if (details != null)
                foreach (var s in details) Add(s, Vector2.zero, Shadow, Shadow);
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
