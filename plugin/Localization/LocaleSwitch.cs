using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace HsPuzzleBridge.Localization
{
    /// <summary>
    /// Switches the CLIENT's locale in-process, for the screenshot tour only (a player's client never changes language
    /// in a session). The sequence is HS_CardExtractor's LocaleSwitcher (credit: the Firestone developer):
    /// SetLocale, then GameStrings.ReloadAll (the game's own words follow), then GameDbf.Load (card names follow).
    /// Nothing is written to the game's options, so a restart is back in the player's language.
    /// Trap: a locale whose Strings\&lt;locale&gt;\ folder is missing resolves every game key to the raw key, so
    /// those are refused (ruRU on this install, enGB everywhere).
    /// </summary>
    internal static class LocaleSwitch
    {
        public static string Current
        {
            get { try { return global::Localization.GetLocaleName(); } catch { return null; } }
        }

        public static bool CanSwitchTo(string code, out string why)
        {
            why = null;
            if (!Enum.IsDefined(typeof(Locale), code ?? "")) { why = "not a client locale"; return false; }
            if (code == Locales.English) return true;
            string folder = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "Strings"), code);
            if (!Directory.Exists(folder)) { why = "no " + folder; return false; }
            return true;
        }

        /// <summary>Switch to code; ok(true) once the client reports it.</summary>
        public static IEnumerator To(string code, Action<bool> ok)
        {
            if (string.Equals(Current, code, StringComparison.Ordinal)) { ok?.Invoke(true); yield break; }
            if (!CanSwitchTo(code, out string why))
            {
                Plugin.Log.LogWarning($"Locale switch: refusing {code}: {why}");
                ok?.Invoke(false);
                yield break;
            }
            bool stepOk = false;
            try
            {
                global::Localization.SetLocale((Locale)Enum.Parse(typeof(Locale), code));
                GameStrings.ReloadAll();
                stepOk = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Locale switch to {code} threw: {e}"); }
            if (!stepOk) { ok?.Invoke(false); yield break; }
            float started = Time.realtimeSinceStartup;
            yield return GameDbf.Load(false, false); // outside a try: a yield can't be in one with a catch
            string now = Current;
            Plugin.Log.LogInfo($"Locale switch: now {now} ({Time.realtimeSinceStartup - started:0.0} s for the card data), " +
                               $"tavern tier \"{GameStrings.Get("GAMEPLAY_BACON_TAVERN_TIER")}\"");
            ok?.Invoke(string.Equals(now, code, StringComparison.Ordinal));
        }
    }
}
