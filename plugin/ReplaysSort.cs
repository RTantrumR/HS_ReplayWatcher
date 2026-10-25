using System;
using System.Linq;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The saved and imported lists' order: a strip button (in those two views) goes round newest first, oldest
    /// first, best place, by hero, longest; each view keeps its own (Library.SortSaved / SortImported). The recent
    /// list stays newest first. A row's Index is its place on the page and its Entry its line in the rows file, so
    /// a new order only moves the built rows (building is the cost, ~75 ms a row); rows not built yet are made at
    /// their place, the ones near the view first.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static readonly string[] SortNames = { "Newest first", "Oldest first", "Best place", "By hero", "Longest" };
        private static Transform _sortButton;

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

        /// <summary>The sort button: the next order round, the shown list re-placed.</summary>
        private static void NextSort()
        {
            var view = ReplayLibrary.Current;
            var setting = view == ReplayLibrary.Saved ? Plugin.SortSaved : view == ReplayLibrary.Imported ? Plugin.SortImported : null;
            if (setting == null) return;
            setting.Value = SortNames[(SortOf(view) + 1) % SortNames.Length];
            ShowSortButton();
            Resort(Current);
        }

        private static void ShowSortButton()
        {
            if (_sortButton == null) return;
            var view = ReplayLibrary.Current;
            bool on = view == ReplayLibrary.Saved || view == ReplayLibrary.Imported;
            _sortButton.gameObject.SetActive(on);
            if (on) _sortButton.GetComponentInChildren<UIBButton>(true)?.SetText(SortNames[SortOf(view)]);
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
