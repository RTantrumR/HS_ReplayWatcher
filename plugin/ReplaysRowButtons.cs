using System;
using System.IO;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The row's own buttons in the place's tab with the star, drawn glyphs (<see cref="Glyph"/>), grey as the
    /// unsaved star: the share arrow at the tab's right end, on every row, puts the game's shareable copy on the
    /// clipboard (<see cref="ReplayShare"/>); the trash bin, on an imported game's row in place of the star (StarX/Z),
    /// removes it from the imported folder (asking first unless the settings say not to; a starred copy stays in
    /// Saved). Each is a flat mesh under the row's root, facing the screen whatever the page's frames are turned
    /// by, placed every frame where the layout says (ShareX/Z, their sizes in row units), clicked through the
    /// game's input (a PegUIElement on its collider).
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static Mesh _trashMesh, _trashHover, _shareMesh, _shareHover;

        private static void UpdateRowButtons(RowParts p)
        {
            if (p.Root == null) return;
            var l = Layout.Current;
            if (p.Share == null)
            {
                if (_shareMesh == null)
                {
                    _shareMesh = Glyph.Build("ShareButton", Glyph.Share(Vector2.zero, 1f), wholeGradient: true, palette: Glyph.GreyPalette);
                    _shareHover = Glyph.Build("ShareButton", Glyph.Share(Vector2.zero, 1f), wholeGradient: true, palette: Glyph.BluePalette);
                }
                string id = p.Id;
                var view = p.Set.View;
                p.Share = RowGlyph(p, "ShareButton", _shareMesh, _shareHover, () => ReplayShare.Share(view, id));
            }
            PlaceRowGlyph(p, p.Share, l.ShareX, l.ShareZ, l.ShareScale);
            if (p.Set.View == ReplayLibrary.Imported)
            {
                if (p.Trash == null)
                {
                    if (_trashMesh == null)
                    {
                        _trashMesh = Glyph.Build("TrashButton", Glyph.Trash(Vector2.zero, 1f), Glyph.TrashSlits(Vector2.zero, 1f),
                                                 palette: Glyph.GreyPalette);
                        _trashHover = Glyph.Build("TrashButton", Glyph.Trash(Vector2.zero, 1f), Glyph.TrashSlits(Vector2.zero, 1f),
                                                  palette: Glyph.RedPalette);
                    }
                    string id = p.Id;
                    p.Trash = RowGlyph(p, "TrashButton", _trashMesh, _trashHover, () => RemoveImported(id));
                }
                PlaceRowGlyph(p, p.Trash, l.StarX, l.StarZ, l.TrashScale); // where the other rows' star is
            }
        }

        /// <summary>A glyph button: its idle mesh, swapped for its hover mesh (its own colour) while the mouse is on it.</summary>
        private static Transform RowGlyph(RowParts p, string name, Mesh mesh, Mesh hover, Action onClick)
        {
            var go = new GameObject(name) { layer = p.Root.gameObject.layer };
            go.transform.SetParent(p.Root, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = GlyphMaterial();
            go.AddComponent<BoxCollider>().size = new Vector3(1f, 0.1f, 1f);
            var element = go.AddComponent<PegUIElement>();
            element.AddEventListener(UIEventType.RELEASE, e => onClick());
            element.AddEventListener(UIEventType.ROLLOVER, e => filter.sharedMesh = hover);
            element.AddEventListener(UIEventType.ROLLOUT, e => filter.sharedMesh = mesh);
            return go.transform;
        }

        private static Material _glyphMaterial;

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
