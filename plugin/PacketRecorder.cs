using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Thread-safe JSONL writer. Packets arrive on the socket thread (S2C) and the main thread (C2S),
    /// so every write goes through one lock. One file per game: a connection to a game that already has a
    /// recording (a reconnect after a crash, a disconnect or a relaunch) carries on in that file after a
    /// "reconnect" line, with the sequence and the clock continued (the clock across the time away).
    /// Recordings stay plain JSONL; the catalog scan compresses them once their game is over.
    /// Connections to the local replay server (watching a replay) aren't recorded unless RecordLocalSessions;
    /// spectating another player's game never is.
    /// </summary>
    internal static class PacketRecorder
    {
        private const double ResumeWithinHours = 3; // no game lasts longer; an older file with the handle is another game

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static Stopwatch _clock;
        private static long _clockOffset;
        private static int _seq;
        private static bool _skipping; // a local session: its packets aren't recorded

        public static void StartSession(string metaJson, uint gameHandle, bool local, bool spectating)
        {
            if (!Plugin.RecorderEnabled.Value) return;
            lock (Gate)
            {
                CloseLocked();
                // Spectating another player isn't recorded: watching a party member after being knocked out joins
                // with the player's own game handle, so it would carry on in (and spoil) the player's recording.
                _skipping = local ? !Plugin.RecordLocalSessions.Value : spectating;
                if (_skipping)
                {
                    if (!local) Plugin.Log.LogInfo($"Spectating game {gameHandle}: not recorded");
                    return;
                }
                Directory.CreateDirectory(Plugin.Recordings);
                string resumed = local ? null : FindRecording(gameHandle);
                if (resumed != null && Resume(resumed))
                {
                    WriteLocked("{\"kind\":\"reconnect\"," + metaJson + "}");
                    Plugin.Log.LogInfo($"Game {gameHandle} again: recording continues in {resumed}");
                    return;
                }
                string name = $"{DateTime.Now:yyyyMMdd_HHmmss}_game{gameHandle}.jsonl";
                string path = Path.Combine(Plugin.Recordings, name);
                _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                _clock = Stopwatch.StartNew();
                _clockOffset = 0;
                _seq = 0;
                WriteLocked("{\"kind\":\"connect\"," + metaJson + "}");
                Plugin.Log.LogInfo("Recording game packets to " + path);
            }
        }

        /// <summary>The newest uncompressed recording of game handle written in the last few hours, or null.</summary>
        private static string FindRecording(uint gameHandle)
        {
            try
            {
                string newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (string path in Directory.GetFiles(Plugin.Recordings, $"*_game{gameHandle}.jsonl"))
                {
                    var t = File.GetLastWriteTimeUtc(path);
                    if (t > newestTime) { newest = path; newestTime = t; }
                }
                return newest != null && (DateTime.UtcNow - newestTime).TotalHours < ResumeWithinHours ? newest : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Looking for a recording to continue: " + e.Message);
                return null;
            }
        }

        /// <summary>Open path for appending after its last whole line, continuing its sequence and clock.</summary>
        private static bool Resume(string path)
        {
            try
            {
                string lastLine = null, wholeLine = null;
                bool endsWithNewline = true;
                using (var reader = new StreamReader(path, new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (line.Length > 0) { wholeLine = lastLine; lastLine = line; }
                }
                using (var fs = File.OpenRead(path))
                    if (fs.Length > 0) { fs.Seek(-1, SeekOrigin.End); endsWithNewline = fs.ReadByte() == '\n'; }
                // A crash can cut the last line short: the reader skips it, so carry on from the line before.
                if (endsWithNewline) wholeLine = lastLine;
                int seq = wholeLine != null ? (int)JsonNumber(wholeLine, "\"seq\":") + 1 : 0;
                long ms = wholeLine != null ? JsonNumber(wholeLine, "\"ms\":") : 0;
                long away = (long)(DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMilliseconds;
                _writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
                if (!endsWithNewline) _writer.WriteLine();
                _clock = Stopwatch.StartNew();
                _clockOffset = ms + Math.Max(0, away);
                _seq = seq;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't continue {path}, starting a new recording: {e.Message}");
                CloseLocked();
                return false;
            }
        }

        private static long JsonNumber(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int j = i;
            while (j < line.Length && char.IsDigit(line[j])) j++;
            return long.TryParse(line.Substring(i, j - i), out long v) ? v : 0;
        }

        public static void Packet(string dir, int type, byte[] body, int offset, int count)
        {
            if (!Plugin.RecorderEnabled.Value) return;
            lock (Gate)
            {
                if (_skipping) return;
                if (_writer == null)
                {
                    // Packets with no GotoGameServer seen first (e.g. plugin loaded mid-game): still keep them.
                    StartSessionUnlocked();
                }
                string b64 = body == null ? "" : Convert.ToBase64String(body, offset, count);
                WriteLocked($"{{\"kind\":\"packet\",\"dir\":\"{dir}\",\"type\":{type},\"len\":{count},\"body\":\"{b64}\"}}");
            }
        }

        public static void Note(string text)
        {
            if (!Plugin.RecorderEnabled.Value) return;
            lock (Gate)
            {
                if (_writer == null || _skipping) return;
                WriteLocked("{\"kind\":\"note\",\"text\":" + Json.Str(text) + "}");
            }
        }

        public static void Close()
        {
            lock (Gate) CloseLocked();
        }

        private static void StartSessionUnlocked()
        {
            Directory.CreateDirectory(Plugin.Recordings);
            string path = Path.Combine(Plugin.Recordings, $"{DateTime.Now:yyyyMMdd_HHmmss}_nosession.jsonl");
            _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            _clock = Stopwatch.StartNew();
            _clockOffset = 0;
            _seq = 0;
            Plugin.Log.LogWarning("Packet seen before any GotoGameServer; recording to " + path);
        }

        // Every line gets a sequence number and ms since the recording started, prepended into the object.
        private static void WriteLocked(string jsonObject)
        {
            string prefix = $"{{\"seq\":{_seq++},\"ms\":{_clockOffset + _clock.ElapsedMilliseconds},";
            _writer.WriteLine(prefix + jsonObject.Substring(1));
        }

        private static void CloseLocked()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); }
            catch (Exception e) { Plugin.Log.LogError("Closing recording failed: " + e); }
            _writer = null;
        }
    }

    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
