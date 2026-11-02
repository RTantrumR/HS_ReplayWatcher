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


        /// <summary>A label (keepLocale: keep the prefab's own per-locale font sizes, for a button that stands beside
        /// the game's own: the lobby's).</summary>
        public static void Label(UberText text, bool keepLocale = false)
        {
            if (text == null) return;
            if (!keepLocale) OwnSize(text);
            ThaiRoom(text);
            text.MinCharacterSize = text.CharacterSize * Smallest;
            text.WordWrap = false;
            text.ResizeToFitAndGrow = false;
            text.ResizeToFit = true;
            Now(text);
        }

        /// <summary>A sentence: wrapped in its box, which gets room for two lines (a copied one-line text's box
        /// would leave the wrap nowhere to go: it shrank to the floor and ended in an ellipsis instead).</summary>
        public static void Wrap(UberText text)
        {
            if (text == null) return;
            text.Height = text.Height * 2f;
            OwnSize(text);
            TwoLinesEverywhere(text);
            text.MinCharacterSize = text.CharacterSize * Smallest;
            text.WordWrap = true;
            text.ResizeToFitAndGrow = false;
            text.ResizeToFit = true;
            Now(text);
        }

        /// <summary>The prefab's per-locale shrinking undone on our copy: Blizzard set it for its own words in that box
        /// (the stats button: 0.75 for esES, esMX, frFR, ptBR, for "Estadísticas completas" and the like), and it made
        /// our short ones ("Ver") small for nothing; our texts shrink themselves when they need to. Growth (Thai's
        /// taller box, its larger glyphs) and the locale's font stay.</summary>
        private static void OwnSize(UberText text)
        {
            UberTextLocalizationSettings settings = null;
            try { settings = HarmonyLib.Traverse.Create(text).Field("m_LocalizedSettings").GetValue<UberTextLocalizationSettings>(); }
            catch (System.Exception e) { Plugin.Log.LogDebug("Fit: " + e.Message); }
            if (settings?.m_LocaleAdjustments == null) return;
            foreach (var a in settings.m_LocaleAdjustments)
            {
                if (a.m_FontSizeModifier < 1f) a.m_FontSizeModifier = 1f;
                if (a.m_ResizeToFitWidthModifier < 1f) a.m_ResizeToFitWidthModifier = 1f;
            }
        }

        /// <summary>Thai's marks above and below the line made a label in a Latin line's box shrink on height alone
        /// (seen 2026-11-02: Thai "Watch" at the floor). As the game's own texts do it (the hero name's thTH entry), a
        /// taller box for thTH only, in the text's per-locale settings, so no other language moves. (A taller box for
        /// every language lifted the top-anchored labels out of their buttons and let the row texts grow.)</summary>
        private static void ThaiRoom(UberText text)
        {
            try
            {
                var field = HarmonyLib.Traverse.Create(text).Field("m_LocalizedSettings");
                var settings = field.GetValue<UberTextLocalizationSettings>();
                if (settings == null) { settings = new UberTextLocalizationSettings(); field.SetValue(settings); }
                var thai = settings.GetLocale(Locale.thTH);
                if (thai != null && thai.m_Height > 0f) return;
                float height = text.Height; // no thTH height yet: the box's own
                if (thai == null) thai = settings.AddLocale(Locale.thTH);
                thai.m_Height = height * 1.6f;
            }
            catch (System.Exception e) { Plugin.Log.LogDebug("Fit: " + e.Message); }
        }

        /// <summary>A per-locale height (the hero name's thTH one, a Thai line tall) overrides the box's own: doubled
        /// as well, or a Thai sentence had no second line to wrap to (seen 2026-11-02: one line, ellipsis).</summary>
        private static void TwoLinesEverywhere(UberText text)
        {
            UberTextLocalizationSettings settings = null;
            try { settings = HarmonyLib.Traverse.Create(text).Field("m_LocalizedSettings").GetValue<UberTextLocalizationSettings>(); }
            catch (System.Exception e) { Plugin.Log.LogDebug("Fit: " + e.Message); }
            if (settings?.m_LocaleAdjustments == null) return;
            foreach (var a in settings.m_LocaleAdjustments)
                if (a.m_Height > 0f) a.m_Height *= 2f;
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
