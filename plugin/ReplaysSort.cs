using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The saved and imported lists' order: a dropdown on the strip (in those two views; a copy of the Options menu's
    /// graphics quality dropdown) offers newest first, oldest first, best place, hero name (A-Z), longest; each view
    /// keeps its own (Library.SortSaved / SortImported). The recent list stays newest first. The menu's chosen item is
    /// written white on its dark bar (the game greys it, which the bar makes hard to read). A row's Index is its place on the page and its Entry its line in the rows file, so
    /// a new order only moves the built rows (building is the cost, ~75 ms a row); rows not built yet are made at
    /// their place, the ones near the view first.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private const string OptionsMenuPrefab = "OptionsMenu.prefab:a6e5621068fd7c8429475b3e1a1aa991";
        private static readonly string[] SortNames = { "Newest first", "Oldest first", "Best place", "Hero name (A-Z)", "Longest" };
        private const string OldHeroSort = "By hero"; // the name before 2026-10-27, as a config may still hold it
        private const float SortTextLift = 1f; // world units towards the camera
        private static Transform _sortT;
        private static DropdownControl _sort;
        private static Vector3 _sortMenuHome; // the menu's place in the dropdown once narrowed (SortMenuX moves it from there)
        private static object _optionsHandle; // the asset handle, kept: a dropped one frees its asset

        /// <summary>The Options menu's graphics quality dropdown, from the prefab asset (never instantiated: its
        /// OptionsMenu would run): copied whole for the sort, its arrow button for the views' arrows.</summary>
        private static DropdownControl OptionsDropdown()
        {
            try
            {
                var handle = AssetLoader.Get().LoadAsset<GameObject>(OptionsMenuPrefab);
                _optionsHandle = handle;
                return handle?.Asset != null ? handle.Asset.GetComponentInChildren<OptionsMenu>(true)?.m_graphicsQuality : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Replays: the Options menu's dropdown: " + e.Message);
                return null;
            }
        }

        private static void CreateSortDropdown()
        {
            if (_strip == null || _sortT != null) return;
            var source = OptionsDropdown();
            if (source == null) { Plugin.Log.LogWarning("Replays: no dropdown in the Options menu; no sort"); return; }
            var go = UnityEngine.Object.Instantiate(source.gameObject, _strip, false);
            go.name = "SortDropdown";
            go.SetActive(true);
            LayerUtils.SetLayer(go, _strip.gameObject.layer);
            _sortT = go.transform;
            _sort = go.GetComponent<DropdownControl>();
            // A copied UberText goes on drawing the original's rendered text object: those go, each makes its own.
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("UberText_RenderObject")) UnityEngine.Object.Destroy(t.gameObject);
            // Its own group label (the menu's "Quality") isn't wanted here.
            var label = go.transform.Find("Label");
            if (label != null) label.gameObject.SetActive(false);
            // A bigger font (SortFont), the template's among them (before the items are made from it).
            var l = Layout.Current;
            foreach (var text in go.GetComponentsInChildren<UberText>(true))
                text.FontSize = Mathf.RoundToInt(text.FontSize * l.SortFont);
            NarrowSort(go.transform, l.SortWidth);
            if (_sort.m_menu != null) _sortMenuHome = _sort.m_menu.transform.localPosition;
            foreach (string name in SortNames) _sort.addItem(name);
            // The box's text centred (the menu's items stay as they are), the view's order in it from the start.
            if (_sort.m_selectedItem?.m_text != null) _sort.m_selectedItem.m_text.Alignment = UberText.AlignmentOptions.Center;
            _sort.setSelection(SortNames[SortOf(ReplayLibrary.Current)]);
            _sort.setItemChosenCallback((chosen, previous) => SortChosen(chosen as string));
            _sort.setMenuShownCallback(shown => { if (shown) LiftSortBars(); });
            PlaceSortDropdown(Layout.Current);
        }

        /// <summary>The items' bars (chosen, hovered) drawn over the menu's frame. They come with mixed sorting orders,
        /// some at the frame's own (51), and one at the frame's order loses to its middle piece (2026-10-27: "Oldest
        /// first" chosen showed no bar, only its top edge above that piece). The texts draw in a later queue anyway.</summary>
        private static void LiftSortBars()
        {
            if (_sort?.m_menu == null || _sort.m_menuItemContainer == null) return;
            int frame = int.MinValue;
            foreach (Transform piece in _sort.m_menu.transform)
            {
                var r = piece.GetComponent<MeshRenderer>();
                if (r != null && piece.name.StartsWith("Dropdown")) frame = Mathf.Max(frame, r.sortingOrder);
            }
            if (frame == int.MinValue) return;
            foreach (var item in _sort.m_menuItemContainer.GetComponentsInChildren<DropdownMenuItem>(true))
                foreach (var r in item.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.GetComponent<MeshFilter>()?.sharedMesh?.name.StartsWith("DropdownSelection") == true)
                        r.sortingOrder = frame + 1;
        }

        private static void PlaceSortDropdown(Layout l)
        {
            if (_sortT == null) return;
            _sortT.localPosition = new Vector3(l.SortX, 1f, l.SortZ);
            _sortT.localRotation = Quaternion.Euler(0f, l.SortRotY, 0f);
            _sortT.localScale = new Vector3(l.SortScale, 1f, l.SortScale);
            if (_sort?.m_menu != null)
            {
                var menu = _sort.m_menu.transform;
                menu.localPosition = _sortMenuHome;
                menu.position += Vector3.right * (l.SortMenuX * Mathf.Abs(_strip.lossyScale.x));
            }
            // The selection's text sits only a hair above the box's face: lifted towards the camera.
            var shown = _sort?.m_selectedItem != null ? _sort.m_selectedItem.m_text : null;
            if (shown != null)
            {
                var p = shown.transform.position;
                shown.transform.position = new Vector3(p.x, _sortT.position.y + SortTextLift, p.z);
            }
        }

        // The parts of the dropdown that keep their shape when it is narrowed: the box's ends (its texture's bolt,
        // and the button's recess with the other bolt), and the menu's stone borders; in the meshes' own units.
        private const float SortCapEnd = 0.6f, SortCapButton = 1.65f, SortMenuCap = 0.35f;

        /// <summary>The box and its menu each SortWidth as wide as the menu's (<see cref="Narrowing"/>: their ends
        /// keep their shape, the button moves in with its end). The menu's frame and items are laid out again from
        /// their meshes whenever it opens, so they follow.</summary>
        private static void NarrowSort(Transform root, float width)
        {
            if (width >= 1f) return;
            var bg = root.GetComponent<MeshFilter>();
            var button = _sort.m_button != null ? _sort.m_button.transform : null;
            var menu = _sort.m_menu != null ? _sort.m_menu.transform : null;
            var label = root.Find("Label");
            var skip = new HashSet<Transform>();
            if (label != null) skip.Add(label);
            if (_sort.m_cancelCatcher != null) skip.Add(_sort.m_cancelCatcher.transform);
            if (bg != null && Narrowing.Span(root, new[] { bg }, out float min, out float max))
            {
                // The button's end keeps more: the recess it sits in.
                bool buttonLow = button == null || root.InverseTransformPoint(button.position).x < (min + max) / 2f;
                var box = Narrowing.Over(root, min, max, buttonLow ? SortCapButton : SortCapEnd,
                                         buttonLow ? SortCapEnd : SortCapButton, (max - min) * (1f - width));
                var rigid = new HashSet<Transform>();
                if (button != null) rigid.Add(button);
                if (menu != null) rigid.Add(menu);
                box.Apply(root, rigid, skip);
            }
            else Plugin.Log.LogWarning("Replays sort: no box mesh; the dropdown keeps its width");
            if (menu == null) return;
            var frame = menu.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith("DropdownTop") ||
                        f.name.StartsWith("DropdownMiddle") || f.name.StartsWith("DropdownBottom")).ToList();
            if (Narrowing.Span(menu, frame, out float mMin, out float mMax))
                Narrowing.Over(menu, mMin, mMax, SortMenuCap, SortMenuCap, (mMax - mMin) * (1f - width))
                         .Apply(menu, new HashSet<Transform>(), skip);
            else Plugin.Log.LogWarning("Replays sort: no menu frame meshes; the menu keeps its width");
        }

        private static int SortOf(ReplayLibrary.View view)
        {
            var setting = view == ReplayLibrary.Saved ? Plugin.SortSaved : view == ReplayLibrary.Imported ? Plugin.SortImported : null;
            if (setting == null) return 0;
            if (setting.Value == OldHeroSort) setting.Value = SortNames[3];
            int i = Array.IndexOf(SortNames, setting.Value);
            return i < 0 ? 0 : i;
        }

        /// <summary>Each entry's place on the page (by its index in the rows file, which is newest first).</summary>
        private static int[] SlotsOf(ReplayLibrary.View view)
        {
            var entries = view.Entries;
            var order = Enumerable.Range(0, entries.Count);
            switch (SortOf(view))
            {
                case 1: order = order.Reverse(); break;
                case 2: order = order.OrderBy(i => entries[i].Place > 0 ? entries[i].Place : 99).ThenBy(i => i); break;
                case 3: order = order.OrderBy(i => HeroName(entries[i].Hero), StringComparer.OrdinalIgnoreCase).ThenBy(i => i); break;
                case 4: order = order.OrderByDescending(i => entries[i].Minutes).ThenByDescending(i => entries[i].Turns).ThenBy(i => i); break;
            }
            var slots = new int[entries.Count];
            int slot = 0;
            foreach (int i in order) slots[i] = slot++;
            return slots;
        }

        private static string HeroName(string card)
        {
            try { return DefLoader.Get()?.GetEntityDef(card)?.GetName() ?? card; }
            catch { return card; }
        }

        /// <summary>An order chosen in the dropdown: kept for the shown view, its list re-placed.</summary>
        private static void SortChosen(string name)
        {
            var view = ReplayLibrary.Current;
            var setting = view == ReplayLibrary.Saved ? Plugin.SortSaved : view == ReplayLibrary.Imported ? Plugin.SortImported : null;
            if (setting == null || name == null || setting.Value == name) return;
            setting.Value = name;
            Resort(Current);
        }

        /// <summary>The dropdown in the saved and imported views, showing the view's order.</summary>
        private static void ShowSortButton()
        {
            if (_sortT == null) return;
            var view = ReplayLibrary.Current;
            bool on = view == ReplayLibrary.Saved || view == ReplayLibrary.Imported;
            _sortT.gameObject.SetActive(on);
            if (on) _sort.setSelection(SortNames[SortOf(view)]);
        }

        /// <summary>A set's rows put in its view's order: places worked out again, built rows moved, the scroll's
        /// extent hung under the new first row (built now if it wasn't).</summary>
        private static void Resort(RowSet set)
        {
            if (set.Slots.Length != set.View.Entries.Count) return; // rebuilt this frame anyway
            set.Slots = SlotsOf(set.View);
            var l = Layout.Current;
            foreach (var p in set.Parts) p.Index = set.Slots[p.Entry];
            for (int i = 0; i < set.Rows.Count; i++) // built rows, set up or still loading
                if (set.Rows[i] != null) set.Rows[i].transform.localPosition = RowPosition(set, set.Slots[i], l);
            int firstEntry = Array.IndexOf(set.Slots, 0);
            if (firstEntry >= 0 && set.Rows[firstEntry] == null)
            {
                set.Queue.Remove(firstEntry);
                CreateRow(set, firstEntry);
            }
            var first = set.Parts.FirstOrDefault(p => p.Index == 0);
            // The scrollable keeps its length until told (as an opening hand's KeepScroll does); a new order is shown
            // from its top.
            if (first != null) KeepScroll(() => UpdateExtent(first, l));
            if (set.Shown && _scroll != null)
            {
                _scroll.SetScrollImmediate(0f);
                _wheelTarget = 0f;
                _wheelShown = 0f;
            }
            Plugin.Log.LogInfo($"Replays: {set.View.Name} sorted {SortNames[SortOf(set.View)]}; first {first?.Id}");
        }

        /// <summary>An item of the sort dropdown's menu (not of any other dropdown).</summary>
        internal static bool IsSortItem(DropdownMenuItem item) => _sortT != null && item != null && item.transform.IsChildOf(_sortT);
    }

    /// <summary>The sort menu's chosen item written white, as a hovered one is: the game greys it, on a bar dark
    /// enough to make that hard to read. Other dropdowns keep the game's colours.</summary>
    [HarmonyPatch(typeof(DropdownMenuItem), nameof(DropdownMenuItem.UpdateTextColor))]
    internal static class SortChosenWhite
    {
        private static void Postfix(DropdownMenuItem __instance)
        {
            if (__instance.m_selected != null && __instance.m_selected.activeInHierarchy && ReplaysUi.IsSortItem(__instance))
                __instance.m_text.TextColor = Color.white;
        }
    }
}
