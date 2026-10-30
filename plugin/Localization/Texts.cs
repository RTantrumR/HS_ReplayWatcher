using System;
using System.Globalization;

namespace HsPuzzleBridge.Localization
{
    /// <summary>The few composed texts: a place, a game's date, a number — each in the language's own form.</summary>
    internal static class Texts
    {
        /// <summary>"2nd place" in the language (keys place.1 … place.8, one phrase each: ordinals differ per language).</summary>
        public static string Place(int place)
        {
            string key = "place." + place;
            string s = Strings.T(key);
            return s == key ? place.ToString(Language.Culture) + "." : s;
        }

        /// <summary>A game's start (the catalog's ISO-like text) as the rows show it: the language's pattern (key
        /// row.date, a .NET format) in its culture; the raw text when it doesn't parse.</summary>
        public static string Date(string start)
        {
            if (string.IsNullOrEmpty(start)) return "";
            if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) return start;
            try { return when.ToString(Strings.T("row.date"), Language.Culture); }
            catch (FormatException) { return when.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture); }
        }

        /// <summary>A number in the language's culture (its decimal mark), with a .NET format such as "0.0".</summary>
        public static string Number(double value, string format) => value.ToString(format, Language.Culture);
    }
}
