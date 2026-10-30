using System;
using System.Globalization;
using BepInEx.Configuration;

namespace HsPuzzleBridge.Localization
{
    /// <summary>
    /// Which catalog the plugin shows: the config's override when set, else the client's own locale, read lazily
    /// (the client sets it at startup, after the plugin loaded: read too early, Localization.GetLocale() says enUS
    /// silently, while GetLocaleName() is still null — so null means "not ready", and nothing is cached). The client
    /// cannot change its language in a session; <see cref="Refresh"/> is for the screenshot tour, which switches it.
    /// </summary>
    internal static class Language
    {
        /// <summary>[Localization] Language: empty follows the client, a client locale code forces that catalog.</summary>
        internal static ConfigEntry<string> Override;

        private static string _code;
        private static CultureInfo _culture;
        private static string _source;

        /// <summary>The catalog's code (a key of <see cref="Locales.All"/>); English until the client's locale is readable.</summary>
        public static string Code { get { Resolve(); return _code ?? Locales.English; } }

        /// <summary>The culture that formats dates and numbers for <see cref="Code"/>.</summary>
        public static CultureInfo Culture { get { Resolve(); return _culture ?? CultureInfo.InvariantCulture; } }

        public static bool Resolved => _code != null;

        /// <summary>Forget the resolution and the loaded catalog; the next read resolves again.</summary>
        public static void Refresh()
        {
            _code = null;
            _culture = null;
            Strings.Reset();
        }

        private static void Resolve()
        {
            if (_code != null) return;
            string forced = Override?.Value?.Trim();
            if (!string.IsNullOrEmpty(forced))
            {
                Set(Locales.CatalogFor(forced), "config " + forced);
                return;
            }
            string client = null;
            try { client = global::Localization.GetLocaleName(); }
            catch (Exception e) { Plugin.Log.LogDebug("Language: the client's locale isn't readable yet: " + e.Message); }
            if (string.IsNullOrEmpty(client)) return; // not set yet: ask again next time
            Set(Locales.CatalogFor(client), "client " + client);
        }

        private static void Set(string code, string source)
        {
            _code = code;
            _source = source;
            try { _culture = CultureInfo.GetCultureInfo(Locales.Info(code).Culture); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Language: no culture for {code}: {e.Message}");
                _culture = CultureInfo.InvariantCulture;
            }
            Plugin.Log.LogInfo($"Language: {code} ({source}), culture {_culture.Name}");
        }

        /// <summary>For the log and bug reports: the catalog and where it came from.</summary>
        public static string Describe() => _code == null ? "unresolved" : $"{_code} ({_source})";
    }
}
