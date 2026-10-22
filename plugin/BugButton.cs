using System;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The bug button's picture: the Battlegrounds Beetle buff art (the game's BeetlesBuff texture) cut to a circle.
    /// The replay bar draws it on a pill of its own; the Replays page has it beside its Back button
    /// (<see cref="ReplaysUi"/>, layout Bug X / Y / Size, in Back button heights). Both start a <see cref="BugReport"/>.
    /// </summary>
    internal static class BugButton
    {
        private const string BeetleTexture = "BeetlesBuff.png:32f8e5760bde8874eb48f4ee25ddfff1";
        private static Texture2D _icon;
        private static bool _tried;

        /// <summary>The round beetle (made once; null if the texture didn't load).</summary>
        public static Texture2D Icon()
        {
            if (_icon != null || _tried) return _icon;
            _tried = true;
            try
            {
                var handle = AssetLoader.Get().LoadAsset<Texture2D>(BeetleTexture);
                var src = handle?.Asset;
                if (src == null) { Plugin.Log.LogWarning("Bug button: no BeetlesBuff texture"); return null; }
                _icon = Round(src);
                handle.Dispose(); // the round copy is ours
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bug button: " + e.Message); }
            return _icon;
        }

        /// <summary>A readable copy (the asset isn't CPU-readable) with everything outside the inscribed circle
        /// transparent, a pixel of soft edge, and mipmaps (it is drawn far smaller than its 512 px).</summary>
        private static Texture2D Round(Texture src)
        {
            int size = src.width;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var active = RenderTexture.active;
            Graphics.Blit(src, rt, Vector2.one, Vector2.zero);
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "BugButton" };
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            float r = size / 2f, edge = size / 128f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                float a = Mathf.Clamp01((r - d) / edge);
                px[y * size + x].a = (byte)(px[y * size + x].a * a);
            }
            tex.SetPixels32(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.Apply(true, true);
            return tex;
        }
    }

    internal static partial class ReplaysUi
    {
        private static Transform _bug, _backButton;
        private static Material _bugMaterial;
        private static readonly Color BugIdle = new Color(0.85f, 0.85f, 0.85f, 1f);

        /// <summary>The page's bug button: a round quad with the beetle, beside the Back button, clicked through the
        /// game's own input (a PegUIElement on its collider).</summary>
        private static void CreateBugButton(Transform back)
        {
            var icon = BugButton.Icon();
            var shader = Shader.Find("Sprites/Default");
            if (back == null || icon == null || shader == null || _bug != null) return;
            _backButton = back;
            var go = new GameObject("BugButton") { layer = back.gameObject.layer };
            _bug = go.transform;
            _bug.SetParent(back.parent, false);
            var mesh = new Mesh { name = "BugButton" };
            // A unit quad in the page's plane (x right, z up the screen, y towards the camera).
            mesh.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _bugMaterial = new Material(shader) { mainTexture = icon, color = BugIdle, name = "BugButton" };
            go.AddComponent<MeshRenderer>().sharedMaterial = _bugMaterial;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 1f);
            var element = go.AddComponent<PegUIElement>();
            element.AddEventListener(UIEventType.RELEASE, e => BugReport.Start());
            element.AddEventListener(UIEventType.ROLLOVER, e => _bugMaterial.color = Color.white);
            element.AddEventListener(UIEventType.ROLLOUT, e => _bugMaterial.color = BugIdle);
            PlaceBugButton(Layout.Current);
        }

        /// <summary>Beside the Back button: its centre BugX / BugZ Back button heights from the button's centre,
        /// BugScale of them tall (the button's size read from its drawn parts).</summary>
        private static void PlaceBugButton(Layout l)
        {
            if (_bug == null || _backButton == null) return;
            var parent = _bug.parent;
            Bounds? world = null;
            foreach (var r in _backButton.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (world == null) world = r.bounds;
                else { var b = world.Value; b.Encapsulate(r.bounds); world = b; }
            }
            if (world == null) return;
            var centre = parent.InverseTransformPoint(world.Value.center);
            float h = Mathf.Abs(parent.InverseTransformVector(world.Value.size).z);
            if (h < 1e-4f) return;
            _bug.localPosition = centre + new Vector3(l.BugX * h, h * 0.2f, l.BugZ * h);
            _bug.localRotation = Quaternion.identity;
            _bug.localScale = Vector3.one * (l.BugScale * h);
        }
    }
}
