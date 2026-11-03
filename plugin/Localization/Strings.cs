using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace HsPuzzleBridge.Localization
{
    /// <summary>
    /// The plugin's own texts, by key, in <see cref="Language.Code"/>. The catalogs are the embedded
    /// <c>lang\&lt;code&gt;.json</c> files (flat key → text); <c>enUS</c> is the key source and the fallback, and a
    /// key no catalog has renders as the key itself (visible, never a throw).
    ///
    /// A value may hold <c>{0}</c> placeholders (<see cref="F"/>, formatted with the language's culture), the game's
    /// plural syntax <c>|4(one,other)</c> (<see cref="N"/>, the forms chosen by the game's own rules per language,
    /// <see cref="Locales.PluralIndex"/>), or be <c>@GAME_KEY</c>: the game's own string for that key
    /// (<c>GameStrings.Get</c>), for the few words the client has in every language.
    /// </summary>
    internal static class Strings
    {
        private static Dictionary<string, string> _en, _active;
        private static string _loadedFor;

        /// <summary>The text for a key: the language's, else English, else the key.</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            Ensure();
            string s;
            if (!(_active.TryGetValue(key, out s) && s != null) && !(_en.TryGetValue(key, out s) && s != null)) return key;
            return s.Length > 1 && s[0] == '@' ? Game(s.Substring(1), key) : s;
        }

        /// <summary>Whether English has the key (a code from the server may be newer than the catalogs).</summary>
        public static bool Has(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            Ensure();
            return _en.ContainsKey(key);
        }

        /// <summary>T(key) with its {n} placeholders filled, in the language's culture.</summary>
        public static string F(string key, params object[] args) => Format(T(key), args);

        /// <summary>T(key) with {0} = n (and {1}… = more), then its |4(…) forms chosen for n.</summary>
        public static string N(string key, int n, params object[] more)
        {
            var args = new object[1 + (more?.Length ?? 0)];
            args[0] = n;
            if (more != null) Array.Copy(more, 0, args, 1, more.Length);
            return Plural(Format(T(key), args), n);
        }

        /// <summary>The English text (for the log and bug reports, which stay English).</summary>
        public static string En(string key, params object[] args)
        {
            Ensure();
            string s;
            if (!(_en.TryGetValue(key, out s) && s != null)) return key;
            if (s.Length > 1 && s[0] == '@') s = Game(s.Substring(1), key);
            return Plural(Format(s, args), args != null && args.Length > 0 && args[0] is int n ? n : 1, Locales.English);
        }

        /// <summary>Forget the loaded catalog (the language changed).</summary>
        public static void Reset() => _loadedFor = null;

        /// <summary>The English keys the loaded language lacks (a diagnostic).</summary>
        public static List<string> Missing()
        {
            Ensure();
            var missing = new List<string>();
            foreach (var k in _en.Keys) if (!_active.ContainsKey(k)) missing.Add(k);
            return missing;
        }

        private static string Format(string text, object[] args)
        {
            if (args == null || args.Length == 0) return text;
            try { return string.Format(Language.Culture, text, args); }
            catch (FormatException e)
            {
                Plugin.Log.LogWarning($"Strings: bad format \"{text}\": {e.Message}");
                return text;
            }
        }

        /// <summary>Every |4(a,b[,c]) in the text replaced by the form for n: the game's syntax and rules.</summary>
        private static string Plural(string text, int n, string code = null)
        {
            int at = text.IndexOf("|4(", StringComparison.Ordinal);
            if (at < 0) return text;
            int index = Locales.PluralIndex(code ?? Language.Code, n);
            var sb = new StringBuilder();
            int from = 0;
            while (at >= 0)
            {
                int close = text.IndexOf(')', at);
                if (close < 0) break;
                string[] forms = text.Substring(at + 3, close - at - 3).Split(',');
                sb.Append(text, from, at - from).Append(forms[Math.Min(index, forms.Length - 1)]);
                from = close + 1;
                at = text.IndexOf("|4(", from, StringComparison.Ordinal);
            }
            sb.Append(text, from, text.Length - from);
            return sb.ToString();
        }

        /// <summary>The game's own string for a key; its English text of ours when the game doesn't have it.</summary>
        private static string Game(string gameKey, string ourKey)
        {
            string s = null;
            try { s = GameStrings.Get(gameKey); }
            catch (Exception e) { Plugin.Log.LogWarning($"Strings: GameStrings.Get({gameKey}): {e.Message}"); }
            if (!string.IsNullOrEmpty(s) && s != gameKey) return s;
            Plugin.Log.LogWarning($"Strings: the game has no {gameKey} (for {ourKey})");
            return ourKey;
        }

        private static void Ensure()
        {
            string code = Language.Code;
            if (_loadedFor == code && _en != null) return;
            if (_en == null) _en = Load(Locales.English) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _active = code == Locales.English ? _en : Load(code) ?? _en;
            _loadedFor = code;
        }

        /// <summary>The catalog for a code: a file BepInEx\plugins\lang\&lt;code&gt;.json first (a translator's or the
        /// fit test's pseudo-locale, without a build), else the one embedded in the DLL; null (logged) when there is
        /// neither or it is unreadable.</summary>
        private static string FileFor(string code) => Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "lang"), code + ".json");

        /// <summary>Whether a catalog file for the code sits beside the DLL (BepInEx\plugins\lang\&lt;code&gt;.json).</summary>
        public static bool HasFile(string code)
        {
            try { return !string.IsNullOrEmpty(code) && code.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && File.Exists(FileFor(code)); }
            catch { return false; }
        }

        private static Dictionary<string, string> Load(string code)
        {
            string file = FileFor(code);
            string from = null;
            string json = null;
            try
            {
                if (File.Exists(file))
                {
                    json = File.ReadAllText(file, Encoding.UTF8);
                    from = file;
                }
                else
                {
                    var asm = Assembly.GetExecutingAssembly();
                    string suffix = ".lang." + code + ".json";
                    foreach (string n in asm.GetManifestResourceNames())
                        if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            using (var stream = asm.GetManifestResourceStream(n))
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                                json = reader.ReadToEnd();
                            from = "the plugin";
                            break;
                        }
                }
                if (json == null)
                {
                    Plugin.Log.LogWarning($"Strings: no catalog {code} in the plugin");
                    return null;
                }
                var table = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                var strict = new Dictionary<string, string>(table, StringComparer.Ordinal);
                Plugin.Log.LogInfo($"Strings: {code} catalog, {strict.Count} keys, from {from}");
                return strict;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Strings: the {code} catalog is unreadable: {e.Message}");
                return null;
            }
        }
    }
}
