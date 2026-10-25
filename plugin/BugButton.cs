using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The bug button's symbol (see <see cref="Glyph"/>): a beetle seen from above, head, body with the wings' split,
    /// six legs and two antennae.
    /// </summary>
    internal static class BugGlyph
    {
        public static IEnumerable<Vector2[]> Shapes(Vector2 centre, float size)
        {
            Vector2 P(float x, float y) => centre + new Vector2(x, y) * size;
            yield return Glyph.Ellipse(P(0f, -0.31f), 0.11f * size, 0.10f * size, 14); // head
            yield return Glyph.Ellipse(P(0f, 0.08f), 0.21f * size, 0.28f * size, 24); // body
            foreach (float side in new[] { -1f, 1f })
            {
                yield return Glyph.Segment(P(side * 0.05f, -0.38f), P(side * 0.17f, -0.50f), 0.06f * size); // antenna
                yield return Glyph.Segment(P(side * 0.14f, -0.08f), P(side * 0.38f, -0.24f), 0.075f * size); // front leg
                yield return Glyph.Segment(P(side * 0.17f, 0.07f), P(side * 0.43f, 0.05f), 0.075f * size); // middle leg
                yield return Glyph.Segment(P(side * 0.14f, 0.24f), P(side * 0.36f, 0.44f), 0.075f * size); // back leg
            }
        }

        /// <summary>The line between the wings, drawn dark over the body.</summary>
        public static Vector2[] Split(Vector2 centre, float size) =>
            Glyph.Segment(centre + new Vector2(0f, -0.14f) * size, centre + new Vector2(0f, 0.34f) * size, 0.045f * size);
    }

    internal static partial class ReplaysUi
    {
        private static Transform _bug, _backButton;
        private static Material _bugMaterial;
        private static bool _bugLogged;

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
            go.AddComponent<MeshFilter>().sharedMesh =
                Glyph.Build("BugButton", BugGlyph.Shapes(Vector2.zero, 1f), new[] { BugGlyph.Split(Vector2.zero, 1f) });
            _bugMaterial = new Material(shader) { mainTexture = Texture2D.whiteTexture, color = Glyph.Idle, name = "BugButton" };
            go.AddComponent<MeshRenderer>().sharedMaterial = _bugMaterial;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 1f);
            var element = go.AddComponent<PegUIElement>();
            element.AddEventListener(UIEventType.RELEASE, e => BugReport.Start());
            element.AddEventListener(UIEventType.ROLLOVER, e => _bugMaterial.color = Color.white);
            element.AddEventListener(UIEventType.ROLLOUT, e => _bugMaterial.color = Glyph.Idle);
            PlaceBugButton(Layout.Current);
        }

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
