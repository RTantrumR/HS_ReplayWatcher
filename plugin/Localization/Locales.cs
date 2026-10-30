using System;

namespace HsPuzzleBridge.Localization
{
    /// <summary>One language the plugin's texts exist in.</summary>
    internal sealed class LocaleInfo
    {
        public readonly string Code;    // the client's locale code, also the catalog's name ("deDE")
        public readonly string Culture; // the .NET culture that formats its dates and numbers ("de-DE")
        public readonly string Native;  // the language's own name
        public readonly string English; // its English name (logs)
        public readonly int PluralForms; // how many forms a |4(...) carries in it (the game's rules, GameStrings.GetPluralIndex)

        public LocaleInfo(string code, string culture, string native, string english, int pluralForms)
        { Code = code; Culture = culture; Native = native; English = english; PluralForms = pluralForms; }
    }

    /// <summary>
    /// The registry: which client locales have a catalog, and the catalog any client locale maps to. Russian maps to
    /// English (the owner's decision: the project's texts default to English), as do enGB and anything unknown.
    /// Plural rules mirror the client's own (GameStrings.GetPluralIndex, client 37.0), so a catalog can carry the
    /// game's |4(one,other) syntax and be read the way the game's string tables are.
    /// </summary>
    internal static class Locales
    {
        public const string English = "enUS";

        public static readonly LocaleInfo[] All =
        {
            new LocaleInfo("enUS", "en-US", "English",             "English",               2),
            new LocaleInfo("deDE", "de-DE", "Deutsch",             "German",                2),
            new LocaleInfo("esES", "es-ES", "Español (EU)",        "Spanish (Spain)",       2),
            new LocaleInfo("esMX", "es-MX", "Español (AL)",        "Spanish (Latin America)", 2),
            new LocaleInfo("frFR", "fr-FR", "Français",            "French",                2),
            new LocaleInfo("itIT", "it-IT", "Italiano",            "Italian",               2),
            new LocaleInfo("jaJP", "ja-JP", "日本語",               "Japanese",              2),
            new LocaleInfo("koKR", "ko-KR", "한국어",               "Korean",                2),
            new LocaleInfo("plPL", "pl-PL", "Polski",              "Polish",                3),
            new LocaleInfo("ptBR", "pt-BR", "Português (BR)",      "Portuguese (Brazil)",   2),
            new LocaleInfo("thTH", "th-TH", "ไทย",                 "Thai",                  2),
            new LocaleInfo("zhCN", "zh-CN", "简体中文",             "Chinese (Simplified)",  2),
            new LocaleInfo("zhTW", "zh-TW", "繁體中文",             "Chinese (Traditional)", 2),
        };

        /// <summary>The catalog for a client locale code: its own when there is one, else English.</summary>
        public static string CatalogFor(string clientCode)
        {
            if (string.IsNullOrEmpty(clientCode)) return English;
            foreach (var l in All)
                if (string.Equals(l.Code, clientCode, StringComparison.OrdinalIgnoreCase)) return l.Code;
            return English; // ruRU, enGB, UNKNOWN, a locale added by a later client
        }

        public static bool Known(string code)
        {
            foreach (var l in All)
                if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static LocaleInfo Info(string code)
        {
            foreach (var l in All)
                if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
            return All[0];
        }

        /// <summary>Which |4(...) form a number takes in a catalog language: the client's own rules.</summary>
        public static int PluralIndex(string code, int n)
        {
            switch (code)
            {
                case "frFR":
                case "koKR":
                case "zhTW":
                case "zhCN":
                    return n <= 1 ? 0 : 1;
                case "plPL":
                    if (n == 1) return 0;
                    if (n == 0) return 2;
                    int tail = n % 100;
                    if (tail >= 11 && tail <= 14) return 2;
                    tail = n % 10;
                    return tail >= 2 && tail <= 4 ? 1 : 2;
                default:
                    return n == 1 ? 0 : 1;
            }
        }
    }
}
