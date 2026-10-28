using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Makes a copied piece of the game's UI narrower without squashing its ends: along one space's x, everything
    /// left of <see cref="Lo"/> moves right by half the cut and everything right of <see cref="Hi"/> moves left by
    /// half, keeping its shape (bolts, borders, a button's recess); only the band between them is shortened. Meshes
    /// are cut at Lo and Hi first (their UVs interpolated on the cut), so the texture in the ends stays as drawn.
    /// Scaling the whole across, as tried first, squashed the dropdown's bolts and its menu's borders.
    /// </summary>
    internal sealed class Narrowing
    {
        public readonly Transform Space;
        public readonly float Lo, Hi, Cut;

        public Narrowing(Transform space, float lo, float hi, float cut)
        {
            Space = space;
            Lo = lo;
            Hi = hi;
            Cut = Mathf.Clamp(cut, 0f, (hi - lo) * 0.95f);
        }

        /// <summary>Over a span [min, max] of the space's x whose ends keep capMin and capMax, the whole made
        /// `cut` narrower.</summary>
        public static Narrowing Over(Transform space, float min, float max, float capMin, float capMax, float cut) =>
            new Narrowing(space, min + capMin, max - capMax, cut);

        public float Map(float x) =>
            x <= Lo ? x + Cut / 2f : x >= Hi ? x - Cut / 2f : Lo + Cut / 2f + (x - Lo) * (Hi - Lo - Cut) / (Hi - Lo);

        private Vector3 MapWorld(Vector3 world)
        {
            var p = Space.InverseTransformPoint(world);
            p.x = Map(p.x);
            return Space.TransformPoint(p);
        }

        private float X(Vector3 world) => Space.InverseTransformPoint(world).x;

        /// <summary>The space's x span of the meshes under the given objects (their bounds' corners).</summary>
        public static bool Span(Transform space, IEnumerable<MeshFilter> filters, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            foreach (var f in filters)
            {
                if (f == null || f.sharedMesh == null) continue;
                var b = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    float x = space.InverseTransformPoint(f.transform.TransformPoint(c)).x;
                    min = Mathf.Min(min, x);
                    max = Mathf.Max(max, x);
                }
            }
            return max > min;
        }

        /// <summary>Everything under `root` (and root's own parts) narrowed: positions moved, meshes cut and
        /// reshaped, box colliders and texts' boxes resized. Objects in `rigid` only move (their parts keep their
        /// shape); those in `skip` (and copied texts' rendered objects) are left alone.</summary>
        public void Apply(Transform root, ICollection<Transform> rigid, ICollection<Transform> skip)
        {
            var parts = new List<Transform>();
            Collect(root, rigid, skip, parts, isRoot: true);
            var before = new Dictionary<Transform, Matrix4x4>();
            foreach (var t in parts) before[t] = t.localToWorldMatrix;
            // Positions first, parents before children (a moved parent carries its children; each is then put
            // where it belongs).
            foreach (var t in parts)
                if (t != root && t != Space) t.position = MapWorld(before[t].MultiplyPoint3x4(Vector3.zero));
            foreach (var t in parts)
            {
                if (rigid.Contains(t)) continue;
                var m0 = before[t];
                var filter = t.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) Reshape(filter, m0);
                foreach (var box in t.GetComponents<BoxCollider>()) Resize(box, m0);
                var text = t.GetComponent<UberText>();
                if (text != null) Resize(text, m0);
            }
        }

        private static void Collect(Transform t, ICollection<Transform> rigid, ICollection<Transform> skip, List<Transform> into, bool isRoot)
        {
            if (!isRoot && (skip.Contains(t) || t.name.StartsWith("UberText_RenderObject"))) return;
            into.Add(t);
            if (rigid.Contains(t) && !isRoot) return;
            foreach (Transform child in t) Collect(child, rigid, skip, into, false);
        }

        /// <summary>The axis of t (local x, y or z) that runs most along the space's x, and how many space units one
        /// local unit of it is.</summary>
        private int AcrossAxis(Matrix4x4 m0, out float unit)
        {
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            int best = 0;
            unit = 0f;
            float bestShare = -1f;
            for (int i = 0; i < 3; i++)
            {
                var v = Space.InverseTransformVector(m0.MultiplyVector(axes[i]));
                float share = v.sqrMagnitude > 0f ? Mathf.Abs(v.x) / v.magnitude : 0f;
                if (share > bestShare) { bestShare = share; best = i; unit = Mathf.Abs(v.x); }
            }
            return best;
        }

        private void Resize(BoxCollider box, Matrix4x4 m0)
        {
            int axis = AcrossAxis(m0, out float unit);
            if (unit <= 0f) return;
            var a = Vector3.zero;
            a[axis] = box.size[axis] / 2f;
            float x0 = X(m0.MultiplyPoint3x4(box.center - a)), x1 = X(m0.MultiplyPoint3x4(box.center + a));
            float m0x = Map(x0), m1x = Map(x1);
            var size = box.size;
            size[axis] = Mathf.Abs(m1x - m0x) / unit;
            box.size = size;
            var centre = Space.InverseTransformPoint(m0.MultiplyPoint3x4(box.center));
            centre.x = (m0x + m1x) / 2f;
            box.center = box.transform.InverseTransformPoint(Space.TransformPoint(centre));
        }

        /// <summary>A text's box (UberText.Width, along its local x, centred on it) mapped like the rest.</summary>
        private void Resize(UberText text, Matrix4x4 m0)
        {
            var v = Space.InverseTransformVector(m0.MultiplyVector(Vector3.right));
            if (v.sqrMagnitude <= 0f || Mathf.Abs(v.x) < 0.5f * v.magnitude) return; // not laid across
            float half = text.Width / 2f;
            float x0 = X(m0.MultiplyPoint3x4(Vector3.left * half)), x1 = X(m0.MultiplyPoint3x4(Vector3.right * half));
            text.Width = Mathf.Abs(Map(x1) - Map(x0)) / Mathf.Abs(v.x);
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>A copy of the filter's mesh, cut at Lo and Hi, its vertices mapped (from where the object was,
        /// to where it is now).</summary>
        private void Reshape(MeshFilter filter, Matrix4x4 m0)
        {
            var src = filter.sharedMesh;
            if (!src.isReadable)
            {
                Plugin.Log.LogWarning($"Narrowing: mesh {src.name} isn't readable; left as it was");
                return;
            }
            var mesh = new Cutter(src);
            var gx = new List<float>();
            foreach (var p in mesh.V) gx.Add(X(m0.MultiplyPoint3x4(p)));
            mesh.Gx = gx;
            var tris = new List<List<int>>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var cut = new List<int>();
                var t = src.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    var polys = new List<List<int>> { new List<int> { t[i], t[i + 1], t[i + 2] } };
                    foreach (float c in new[] { Lo, Hi })
                    {
                        var next = new List<List<int>>();
                        foreach (var poly in polys)
                        {
                            var below = mesh.Clip(poly, c, true);
                            var above = mesh.Clip(poly, c, false);
                            if (below.Count >= 3) next.Add(below);
                            if (above.Count >= 3) next.Add(above);
                        }
                        polys = next;
                    }
                    foreach (var poly in polys)
                        for (int k = 1; k + 1 < poly.Count; k++) { cut.Add(poly[0]); cut.Add(poly[k]); cut.Add(poly[k + 1]); }
                }
                tris.Add(cut);
            }
            var now = filter.transform.worldToLocalMatrix;
            for (int i = 0; i < mesh.V.Count; i++)
                mesh.V[i] = now.MultiplyPoint3x4(MapWorld(m0.MultiplyPoint3x4(mesh.V[i])));
            filter.sharedMesh = mesh.Build(src.name + "_narrow", tris);
        }

        /// <summary>A mesh's vertex data as lists, so cuts can add vertices (every channel interpolated).</summary>
        private sealed class Cutter
        {
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector4> T = new List<Vector4>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<Vector2> U0 = new List<Vector2>(), U1 = new List<Vector2>();
            public List<float> Gx;

            public Cutter(Mesh m)
            {
                m.GetVertices(V);
                m.GetNormals(N);
                m.GetTangents(T);
                m.GetColors(C);
                m.GetUVs(0, U0);
                m.GetUVs(1, U1);
            }

            private int Lerp(int a, int b, float t)
            {
                V.Add(Vector3.Lerp(V[a], V[b], t));
                if (N.Count > 0) N.Add(Vector3.Lerp(N[a], N[b], t).normalized);
                if (T.Count > 0) T.Add(Vector4.Lerp(T[a], T[b], t));
                if (C.Count > 0) C.Add(Color.Lerp(C[a], C[b], t));
                if (U0.Count > 0) U0.Add(Vector2.Lerp(U0[a], U0[b], t));
                if (U1.Count > 0) U1.Add(Vector2.Lerp(U1[a], U1[b], t));
                Gx.Add(Mathf.Lerp(Gx[a], Gx[b], t));
                return V.Count - 1;
            }

            /// <summary>The part of a convex polygon on one side of x = c (Sutherland-Hodgman; winding kept).</summary>
            public List<int> Clip(List<int> poly, float c, bool below)
            {
                var result = new List<int>();
                for (int k = 0; k < poly.Count; k++)
                {
                    int a = poly[k], b = poly[(k + 1) % poly.Count];
                    float fa = Gx[a] - c, fb = Gx[b] - c;
                    bool inA = below ? fa <= 0f : fa >= 0f;
                    if (inA) result.Add(a);
                    if (fa * fb < 0f) result.Add(Lerp(a, b, fa / (fa - fb)));
                }
                return result;
            }

            public Mesh Build(string name, List<List<int>> tris)
            {
                var m = new Mesh { name = name };
                if (V.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(V);
                if (N.Count == V.Count) m.SetNormals(N);
                if (T.Count == V.Count) m.SetTangents(T);
                if (C.Count == V.Count) m.SetColors(C);
                if (U0.Count == V.Count) m.SetUVs(0, U0);
                if (U1.Count == V.Count) m.SetUVs(1, U1);
                m.subMeshCount = tris.Count;
                for (int s = 0; s < tris.Count; s++) m.SetTriangles(tris[s], s);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
