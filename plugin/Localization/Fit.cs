namespace HsPuzzleBridge.Localization
{
    /// <summary>
    /// How a text of ours behaves when its language runs longer than English: a label keeps to one line and shrinks
    /// its font to its box (never grows: the English size is the ceiling); a sentence wraps in its box and shrinks
    /// only if the lines still don't fit. UberText does both itself (ResizeToFit, WordWrap) once told — against its
    /// box (Width), so the box must be the room the text really has: for a row's texts that is the span to the
    /// neighbouring text, not the plate under it (the plates overlap; seen 2026-10-30 with the pseudo-locale).
    /// </summary>
    internal static class Fit
    {
        /// <summary>How far a text may shrink: to this share of its size (UberText's floor, MinCharacterSize, which a
        /// prefab may keep near the size itself).</summary>
        private const float Smallest = 0.55f;

        public static void Label(UberText text)
        {
            if (text == null) return;
            text.MinCharacterSize = text.CharacterSize * Smallest;
            text.WordWrap = false;
            text.ResizeToFitAndGrow = false;
            text.ResizeToFit = true;
            Now(text);
        }

        public static void Wrap(UberText text)
        {
            if (text == null) return;
            text.MinCharacterSize = text.CharacterSize * Smallest;
            text.WordWrap = true;
            text.ResizeToFitAndGrow = false;
            text.ResizeToFit = true;
            Now(text);
        }

        /// <summary>Lay the text out again now, hidden or not. UberText's own UpdateText() only marks an ACTIVE text
        /// for its next frame, so a box or fit set on a hidden one (the rows are built hidden, the strip's buttons
        /// before the strip shows) would keep the layout of its first render.</summary>
        public static void Now(UberText text)
        {
            if (text == null) return;
            try { text.UpdateNow(true); }
            catch (System.Exception e) { Plugin.Log.LogDebug("Fit: " + e.Message); }
        }
    }
}
