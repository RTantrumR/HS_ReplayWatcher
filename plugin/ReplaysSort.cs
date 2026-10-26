using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The saved and imported lists' order: a dropdown on the strip (in those two views; a copy of the Options menu's
    /// graphics quality dropdown) offers newest first, oldest first, best place, by hero, longest; each view keeps
    /// its own (Library.SortSaved / SortImported). The recent list stays newest first. A row's Index is its place on the page and its Entry its line in the rows file, so
    /// a new order only moves the built rows (building is the cost, ~75 ms a row); rows not built yet are made at
    /// their place, the ones near the view first.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private const string OptionsMenuPrefab = "OptionsMenu.prefab:a6e5621068fd7c8429475b3e1a1aa991";
        private static readonly string[] SortNames = { "Newest first", "Oldest first", "Best place", "By hero", "Longest" };
        private const float SortTextLift = 1f; // world units towards the camera
        private static Transform _sortT;
        private static DropdownControl _sort;
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
            foreach (string name in SortNames) _sort.addItem(name);
            // The box's text centred (the menu's items stay as they are), the view's order in it from the start.
            if (_sort.m_selectedItem?.m_text != null) _sort.m_selectedItem.m_text.Alignment = UberText.AlignmentOptions.Center;
            _sort.setSelection(SortNames[SortOf(ReplayLibrary.Current)]);
            _sort.setItemChosenCallback((chosen, previous) => SortChosen(chosen as string));
            PlaceSortDropdown(Layout.Current);
        }

        private static void PlaceSortDropdown(Layout l)
        {
            if (_sortT == null) return;
            _sortT.localPosition = new Vector3(l.SortX, 1f, l.SortZ);
            _sortT.localRotation = Quaternion.Euler(0f, l.SortRotY, 0f);
            _sortT.localScale = new Vector3(l.SortScale, 1f, l.SortScale);
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
    }
}
