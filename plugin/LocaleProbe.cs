using System;
using System.Globalization;

namespace HsPuzzleBridge
{
    /// <summary>One log line, at the Replays page's first open, with what the plugin's localization rests on: the
    /// client's locale and culture (Blizzard.T5.Localization), a game string read in the lobby (GAMEPLAY category),
    /// the game's own plural formatter on a text of ours, and Mono's culture data for dates. Kept for bug reports.</summary>
    internal static class LocaleProbe
    {
        public static void Log()
        {
            try
            {
                string name = Localization.GetLocaleName();
                var culture = Localization.GetCultureInfo();
                string tier = GameStrings.Get("GAMEPLAY_BACON_TAVERN_TIER");
                string one = GameStrings.FormatLocalizedStringWithPlurals("{0} |4(turn,turns)", GameStrings.MakePlurals(1), 1);
                string two = GameStrings.FormatLocalizedStringWithPlurals("{0} |4(turn,turns)", GameStrings.MakePlurals(2), 2);
                var when = new DateTime(2026, 10, 30, 14, 5, 0);
                string de = when.ToString("d. MMM, HH:mm", new CultureInfo("de-DE"));
                string pl = when.ToString("d MMM, HH:mm", new CultureInfo("pl-PL"));
                string ja = when.ToString("M月d日 HH:mm", new CultureInfo("ja-JP"));
                string number = (12.5).ToString("0.0", new CultureInfo("de-DE"));
                Plugin.Log.LogInfo($"Locale probe: client {name ?? "(null)"} = {Localization.GetLocale()}, culture {culture?.Name ?? "(null)"}; " +
                                   $"tavern tier \"{tier}\"; plurals \"{one}\" / \"{two}\"; dates de \"{de}\", pl \"{pl}\", ja \"{ja}\"; number de {number}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Locale probe failed: " + e);
            }
        }
    }
}
