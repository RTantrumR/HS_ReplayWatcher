using System;
using System.IO;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The row's own buttons beside the star, drawn glyphs (<see cref="Glyph"/>) on the card: the trash bin on an
    /// imported game's row removes it from the imported folder (asking first unless the settings say not to; a
    /// starred copy stays in Saved). Each is a flat mesh under the row's root, facing the screen whatever the page's
    /// frames are turned by, placed every frame where the layout says (TrashX/Z, its size TrashScale in row units),
    /// clicked through the game's input (a PegUIElement on its collider).
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static Mesh _trashMesh;

        private static void UpdateRowButtons(RowParts p)
        {
            if (p.Root == null) return;
            var l = Layout.Current;
            if (p.Set.View == ReplayLibrary.Imported)
            {
                if (p.Trash == null)
                {
                    if (_trashMesh == null)
                        _trashMesh = Glyph.Build("TrashButton", Glyph.Trash(Vector2.zero, 1f), Glyph.TrashSlits(Vector2.zero, 1f));
                    string id = p.Id;
                    p.Trash = RowGlyph(p, "TrashButton", _trashMesh, () => RemoveImported(id));
                }
                PlaceRowGlyph(p, p.Trash, l.TrashX, l.TrashZ, l.TrashScale);
            }
        }

        private static Transform RowGlyph(RowParts p, string name, Mesh mesh, Action onClick)
        {
            var shader = Shader.Find("Sprites/Default");
            var go = new GameObject(name) { layer = p.Root.gameObject.layer };
            go.transform.SetParent(p.Root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(shader) { mainTexture = Texture2D.whiteTexture, color = Glyph.Idle, name = name };
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.AddComponent<BoxCollider>().size = new Vector3(1f, 0.1f, 1f);
            var element = go.AddComponent<PegUIElement>();
            element.AddEventListener(UIEventType.RELEASE, e => onClick());
            element.AddEventListener(UIEventType.ROLLOVER, e => material.color = Color.white);
            element.AddEventListener(UIEventType.ROLLOUT, e => material.color = Glyph.Idle);
            return go.transform;
        }

        /// <summary>Centred at (x, z) in the row's units, size of them wide, lifted towards the camera as the Watch
        /// button is, facing the screen (the page's plane is the world's XZ, y towards the camera).</summary>
        private static void PlaceRowGlyph(RowParts p, Transform glyph, float x, float z, float size)
        {
            glyph.position = p.Root.TransformPoint(new Vector3(x, 1f, z));
            glyph.rotation = Quaternion.identity;
            glyph.localScale = Vector3.one * size;
        }

        /// <summary>An imported game out of the imported folder: its row goes at once, the scan follows.</summary>
        private static void RemoveImported(string id)
        {
            if (FolderPicker.Open) return;
            string path = ReplayLibrary.RecordingIn(ReplayLibrary.ImportedDir, id);
            if (path == null) { ReplayServerProcess.ScanCatalog(); return; }
            string text = ReplayLibrary.SavedIds.Contains(id)
                ? "The replay will be removed from your imported replays. Its starred copy stays in your saved replays."
                : "The replay will be deleted from your imported replays.";
            AskUnless(Plugin.NoAskRemove.Value, "Remove from imported?", text, "Remove", () =>
            {
                try
                {
                    File.Delete(path);
                    Plugin.Log.LogInfo("Replays: removed imported " + id);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Replays: couldn't remove {id}: {e.Message}");
                    Alert("Couldn't remove the replay", e.Message);
                    return;
                }
                ReplayLibrary.SetRow(ReplayLibrary.Imported, id, null);
                ReplayServerProcess.ScanCatalog();
            });
        }
    }
}
