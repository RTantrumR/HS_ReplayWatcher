using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The BG stats pages (the stock Full Stats frame and the Replays page, a copy of it) slide in from below and
    /// out downward by about one screen. Their scroll contents scrolled above the window stay rendered just past
    /// the screen's top, so during the slide they came through over the lobby: the long parchment and the rows on
    /// close, the rows above the window on open (with the remembered scroll), the more the further the list was
    /// scrolled. While a page's scroll window is moving or off screen, every renderer of its contents that
    /// reaches above the window's top edge is switched off, and the parchment (PageBg, a readable runtime mesh)
    /// is cut at that edge instead; all is put back once the window rests on screen. At rest nothing changes:
    /// the frame's top covers that strip and the rest is off screen.
    /// </summary>
    internal static class PageSlideGuard
    {
        private const float RestSeconds = 0.12f;

        private sealed class Guard
        {
            public UIBScrollable Scroll;
            public string Name;
            public string Why; // why it is on (for the log)
            public int Frames; // frames since it was last rescanned
            public Vector3 Last;
            public bool WasActive, On;
            public float QuietSince;
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public readonly List<KeyValuePair<MeshFilter, Mesh>> Clipped = new List<KeyValuePair<MeshFilter, Mesh>>();
            public readonly Dictionary<MeshFilter, Mesh> Cut = new Dictionary<MeshFilter, Mesh>();
        }

        private static readonly Dictionary<UIBScrollable, Guard> Guards = new Dictionary<UIBScrollable, Guard>();
        private static float _nextSearch;
        private static WidgetInstance _stockFrame;

        /// <summary>Called every frame from the plugin's LateUpdate: after the frame's animations moved the page,
        /// before it is drawn.</summary>
        public static void LateUpdate()
        {
            if (_stockFrame == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 5f;
                Search();
            }
            List<UIBScrollable> gone = null;
            foreach (var pair in Guards)
            {
                var g = pair.Value;
                if (g.Scroll == null) { (gone ?? (gone = new List<UIBScrollable>())).Add(pair.Key); continue; }
                try { Step(g); }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Page slide guard: " + e.Message);
                    Release(g);
                }
            }
            if (gone != null) foreach (var s in gone) Guards.Remove(s);
        }

        /// <summary>The stock stats frame's scrollables (solo and duos). The search walks the lobby (18 ms), so it
        /// runs only until the frame is found, at most every 5 s. The Replays page hands its own in
        /// (<see cref="Watch"/>).</summary>
        private static void Search()
        {
            var display = BaconDisplay.Get();
            if (display == null) return;
            foreach (var w in display.GetComponentsInChildren<WidgetInstance>(true))
            {
                if (w.name != "BaconStatsFrame_PC") continue;
                _stockFrame = w;
                foreach (var s in w.GetComponentsInChildren<UIBScrollable>(true)) Watch(s, w.name);
                return;
            }
        }

        /// <summary>Guard this scrollable (the Replays page's, once it exists).</summary>
        internal static void Watch(UIBScrollable s, string owner)
        {
            if (s != null && s.m_ScrollBounds != null && !Guards.ContainsKey(s))
                Guards[s] = new Guard { Scroll = s, Name = owner + "/" + s.name };
        }

        private static void Step(Guard g)
        {
            var s = g.Scroll;
            var content = s.ScrollObject;
            bool active = content != null && content.activeInHierarchy && s.m_ScrollBounds.gameObject.activeInHierarchy;
            if (!active)
            {
                if (g.On) Release(g);
                g.WasActive = false;
                return;
            }
            var window = s.m_ScrollBounds.transform;
            var pos = window.position;
            bool moved = !g.WasActive || (pos - g.Last).sqrMagnitude > 1e-6f;
            g.WasActive = true;
            g.Last = pos;
            bool offscreen = !TopOnScreen(s, out _);
            bool restless = moved || offscreen;
            if (restless) g.QuietSince = Time.unscaledTime;
            if (restless || (g.On && Time.unscaledTime - g.QuietSince < RestSeconds))
            {
                string why = moved ? "moving" : offscreen ? "off screen" : "settling";
                if (!g.On || why != g.Why) Plugin.Log.LogDebug($"Page slide guard {g.Name}: {why}");
                g.Why = why;
                // While it slides, every frame (what turns up meanwhile); resting off screen, every 10th frame.
                if (!g.On || moved || ++g.Frames >= 10)
                {
                    g.Frames = 0;
                    Hide(g, content.transform, WindowTop(s.m_ScrollBounds));
                }
            }
            else if (g.On)
            {
                Plugin.Log.LogDebug($"Page slide guard {g.Name}: at rest, {g.Hidden.Count} renderer(s) and {g.Cut.Count} cut mesh(es) back");
                Release(g);
            }
        }

        /// <summary>The window's top edge (world z; the lists run down the page towards -z).</summary>
        private static float WindowTop(BoxCollider box)
        {
            var t = box.transform;
            float top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = box.center + Vector3.Scale(box.size * 0.5f,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                top = Mathf.Max(top, t.TransformPoint(corner).z);
            }
            return top;
        }

        private static bool TopOnScreen(UIBScrollable s, out Camera camera)
        {
            camera = null;
            try { camera = Traverse.Create(s).Method("GetScrollCamera").GetValue<Camera>(); } catch { }
            if (camera == null) return true; // nothing to test against: treat as resting
            var box = s.m_ScrollBounds;
            var centre = box.transform.TransformPoint(box.center);
            var top = new Vector3(centre.x, centre.y, WindowTop(box));
            var v = camera.WorldToViewportPoint(top);
            return v.z > 0f && v.x >= -0.01f && v.x <= 1.01f && v.y >= -0.01f && v.y <= 1.01f;
        }

        /// <summary>Everything of the contents above `top` off (renderers that only cross it and have a readable
        /// mesh, the parchment, cut at it instead); run every frame while on, for what turns up meanwhile.</summary>
        private static void Hide(Guard g, Transform content, float top)
        {
            g.On = true;
            foreach (var r in content.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                var b = r.bounds;
                if (b.max.z <= top + 0.01f) continue;
                var filter = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
                if (b.min.z < top && filter != null && r.name == "PageBg")
                {
                    if (CutAt(g, filter, top)) continue;
                }
                r.enabled = false;
                g.Hidden.Add(r);
            }
        }

        /// <summary>The filter's mesh replaced with a copy cut at world z = top (kept below it). False when it can't
        /// be (an unreadable mesh). Re-cut when the page moved the line within the mesh (it doesn't while the
        /// contents and the window slide together) or the mesh was swapped (the parchment refitted).</summary>
        private static bool CutAt(Guard g, MeshFilter filter, float top)
        {
            var current = filter.sharedMesh;
            if (current == null) return false;
            if (g.Cut.TryGetValue(filter, out var cut) && current == cut) return true;
            if (!current.isReadable) return false;
            var mesh = Clip(current, filter.transform.localToWorldMatrix, top);
            if (mesh == null) return false;
            g.Clipped.Add(new KeyValuePair<MeshFilter, Mesh>(filter, current));
            g.Cut[filter] = mesh;
            filter.sharedMesh = mesh;
            return true;
        }

        private static void Release(Guard g)
        {
            foreach (var r in g.Hidden) if (r != null) r.enabled = true;
            g.Hidden.Clear();
            for (int i = g.Clipped.Count - 1; i >= 0; i--)
            {
                var filter = g.Clipped[i].Key;
                if (filter != null && g.Cut.TryGetValue(filter, out var cut) && filter.sharedMesh == cut)
                    filter.sharedMesh = g.Clipped[i].Value;
            }
            foreach (var cut in g.Cut.Values) if (cut != null) Object.Destroy(cut);
            g.Clipped.Clear();
            g.Cut.Clear();
            g.On = false;
        }

        /// <summary>A copy of the mesh with every triangle cut to the part where the world z is at most `top`
        /// (positions and UVs interpolated along the cut edges).</summary>
        private static Mesh Clip(Mesh source, Matrix4x4 toWorld, float top)
        {
            var verts = source.vertices;
            var uvs = source.uv;
            bool hasUv = uvs != null && uvs.Length == verts.Length;
            var tris = source.triangles;
            var outVerts = new List<Vector3>();
            var outUv = new List<Vector2>();
            var outTris = new List<int>();
            var polyV = new List<Vector3>(6);
            var polyU = new List<Vector2>(6);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                polyV.Clear();
                polyU.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t + k], b = tris[t + (k + 1) % 3];
                    float fa = toWorld.MultiplyPoint3x4(verts[a]).z - top, fb = toWorld.MultiplyPoint3x4(verts[b]).z - top;
                    if (fa <= 0f)
                    {
                        polyV.Add(verts[a]);
                        polyU.Add(hasUv ? uvs[a] : Vector2.zero);
                    }
                    if ((fa <= 0f) != (fb <= 0f))
                    {
                        float x = fa / (fa - fb);
                        polyV.Add(Vector3.Lerp(verts[a], verts[b], x));
                        polyU.Add(hasUv ? Vector2.Lerp(uvs[a], uvs[b], x) : Vector2.zero);
                    }
                }
                if (polyV.Count < 3) continue;
                int first = outVerts.Count;
                outVerts.AddRange(polyV);
                outUv.AddRange(polyU);
                for (int k = 1; k + 1 < polyV.Count; k++)
                {
                    outTris.Add(first);
                    outTris.Add(first + k);
                    outTris.Add(first + k + 1);
                }
            }
            var mesh = new Mesh { name = source.name + " (cut)" };
            mesh.SetVertices(outVerts);
            if (hasUv) mesh.SetUVs(0, outUv);
            mesh.SetTriangles(outTris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
