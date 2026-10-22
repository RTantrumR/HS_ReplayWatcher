using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Dev tool: write the scene's UI tree to BepInEx/ui_dump_N.txt on <see cref="Plugin.UiDumpKey"/> (with Ctrl).
    ///
    /// The BG lobby's pages and buttons are prefab-driven (state collections, events, nested widget instances),
    /// so the decompile alone can't say which objects open what. The dump lists every GameObject under the scene
    /// roots with its components; for the UI framework's components (widgets, visual controllers, clickables,
    /// buttons, lists, cards, texts) it also walks their fields by reflection, a few levels deep, so state names,
    /// state actions (event names, targets), asset strings and bound data models come out without knowing field
    /// names in advance. Unity objects met in fields are printed as name + type only.
    /// </summary>
    internal static class UiDump
    {
        private const int MaxDepth = 4, MaxItems = 40;
        private static int _count;

        private static readonly string[] Detailed =
        {
            "VisualController", "Clickable", "WidgetInstance", "WidgetTemplate", "Listable", "UIBButton",
            "Card", "UIBScrollable", "UberText", "PegUIElement", "WidgetTransform", "BaconDisplay",
        };

        public static void Update()
        {
            if (!Input.GetKeyDown(Plugin.UiDumpKey.Value)) return;
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"UI dump, scene mode {SceneMgr.Get()?.GetMode()}, screen {Screen.width}x{Screen.height}");
                // With Shift: only the plugin's own lobby objects (the full scene is ~30 MB).
                bool ours = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                foreach (var root in ours ? Ours() : Roots())
                    Walk(root.transform, "", sb);
                string path = Path.Combine(Paths.BepInExRootPath, $"ui_dump_{++_count}.txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Plugin.Log.LogInfo($"UI dump written to {path} ({sb.Length / 1024} KB)");
                UIStatus.Get()?.AddInfo($"UI dump {_count} written");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("UI dump: " + e);
            }
        }

        private static IEnumerable<GameObject> Roots()
        {
            var seen = new HashSet<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                var root = t.root.gameObject;
                if (seen.Add(root)) yield return root;
            }
        }

        private static IEnumerable<GameObject> Ours()
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (t.name == "ReplaysPage" || t.name == "ReplaysButton") yield return t.gameObject;
        }

        private static void Walk(Transform t, string indent, StringBuilder sb)
        {
            var go = t.gameObject;
            sb.Append(indent).Append(go.activeInHierarchy ? "+ " : "- ").Append(go.name)
              .Append($"  pos={Fmt(t.localPosition)} scale={Fmt(t.localScale)} layer={go.layer}");
            var world = t.position;
            sb.Append($" world={Fmt(world)}").AppendLine();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                var type = c.GetType();
                sb.Append(indent).Append("    [").Append(type.FullName).Append(']');
                var mb = c as Behaviour;
                if (mb != null && !mb.enabled) sb.Append(" (disabled)");
                sb.AppendLine();
                if (IsDetailed(type)) Fields(c, indent + "      ", sb, 0, new HashSet<object>());
                else if (c is Renderer r) RendererInfo(r, indent + "      ", sb);
                else if (c is MeshFilter mf) MeshInfo(mf.sharedMesh, indent + "      ", sb);
            }
            foreach (Transform child in t)
                Walk(child, indent + "  ", sb);
        }

        /// <summary>What a renderer draws and in which order: materials, shader, queue, texture and its tiling.</summary>
        private static void RendererInfo(Renderer r, string indent, StringBuilder sb)
        {
            sb.Append(indent).Append($"enabled={r.enabled} bounds={Fmt(r.bounds.center)} size={Fmt(r.bounds.size)}")
              .Append($" sorting={r.sortingLayerID}/{r.sortingOrder}").AppendLine();
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { sb.Append(indent).AppendLine("material null"); continue; }
                sb.Append(indent).Append($"material '{m.name}' shader '{m.shader?.name}' queue={m.renderQueue}");
                if (m.HasProperty("_Color")) sb.Append($" color={m.color}");
                var tex = m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (tex != null)
                    sb.Append($" tex '{tex.name}' {tex.width}x{tex.height} st={m.mainTextureScale}/{m.mainTextureOffset}");
                sb.AppendLine();
            }
        }

        private static void MeshInfo(Mesh mesh, string indent, StringBuilder sb)
        {
            if (mesh == null) { sb.Append(indent).AppendLine("mesh null"); return; }
            sb.Append(indent).Append($"mesh '{mesh.name}' verts={mesh.vertexCount} bounds={Fmt(mesh.bounds.center)}")
              .Append($" size={Fmt(mesh.bounds.size)}");
            if (mesh.isReadable && mesh.vertexCount > 0 && mesh.vertexCount <= 64)
            {
                var uv = mesh.uv;
                if (uv.Length > 0)
                {
                    float u0 = 1e9f, v0 = 1e9f, u1 = -1e9f, v1 = -1e9f;
                    foreach (var q in uv) { u0 = Mathf.Min(u0, q.x); v0 = Mathf.Min(v0, q.y); u1 = Mathf.Max(u1, q.x); v1 = Mathf.Max(v1, q.y); }
                    sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        " uv=({0:0.###}..{1:0.###}, {2:0.###}..{3:0.###})", u0, u1, v0, v1));
                    var vs = mesh.vertices;
                    sb.Append(" first:");
                    for (int i = 0; i < Math.Min(4, vs.Length); i++)
                        sb.Append($" {Fmt(vs[i])}->{uv[i].x:0.##},{uv[i].y:0.##}");
                }
            }
            else if (!mesh.isReadable) sb.Append(" (not readable)");
            sb.AppendLine();
        }

        private static bool IsDetailed(Type type)
        {
            for (var x = type; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
                if (Array.IndexOf(Detailed, x.Name) >= 0) return true;
            return false;
        }

        private static void Fields(object obj, string indent, StringBuilder sb, int depth, HashSet<object> seen)
        {
            if (obj == null || depth > MaxDepth || !seen.Add(obj)) return;
            for (var type = obj.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(object);
                 type = type.BaseType)
            {
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsSubclassOf(typeof(Delegate))) continue;
                    object v;
                    try { v = f.GetValue(obj); } catch { continue; }
                    sb.Append(indent).Append(f.Name).Append(" = ").Append(Short(v)).AppendLine();
                    if (v == null || v is string || v is UnityEngine.Object || f.FieldType.IsPrimitive || f.FieldType.IsEnum)
                        continue;
                    if (v is IEnumerable list)
                    {
                        int i = 0;
                        foreach (var item in list)
                        {
                            if (i++ >= MaxItems) { sb.Append(indent).AppendLine("  ..."); break; }
                            sb.Append(indent).Append("  [").Append(i - 1).Append("] ").Append(Short(item)).AppendLine();
                            if (item != null && !(item is string) && !(item is UnityEngine.Object) && !item.GetType().IsPrimitive)
                                Fields(item, indent + "    ", sb, depth + 1, seen);
                        }
                    }
                    else if (!f.FieldType.IsValueType || f.FieldType.Namespace?.StartsWith("Hearthstone") == true)
                    {
                        Fields(v, indent + "  ", sb, depth + 1, seen);
                    }
                }
            }
        }

        private static string Short(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object o) return o == null ? "null(destroyed)" : $"<{o.GetType().Name} '{o.name}'>";
            if (v is string s) return "\"" + (s.Length > 200 ? s.Substring(0, 200) + "…" : s) + "\"";
            string text;
            try { text = v.ToString(); } catch { text = "?"; }
            return text.Length > 200 ? text.Substring(0, 200) + "…" : text;
        }

        private static string Fmt(Vector3 v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.##}; {1:0.##}; {2:0.##})", v.x, v.y, v.z);
    }
}
