using System;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The game's own reconnect popup, played where the recorded client dropped and came back: "Reconnecting" /
    /// "You were disconnected from your game. Reconnecting..." for the first half, then (as
    /// ReconnectMgr_UI.ChangeGameplayDialogToReconnected does, by UpdateInfo on the same popup) "Reconnected" /
    /// "You were reconnected to your game. Resuming...", gone after <see cref="Seconds"/>. The texts are the game's
    /// strings, so they follow its language. Unlike the real one it has no Exit Game button. Meanwhile the server
    /// resets the board to the reconnected state (the sand wipe) halfway through.
    /// </summary>
    internal static class ReconnectPopup
    {
        public const float Seconds = 3f; // as the server's 2 x RECONNECT_HALF_S

        private static AlertPopup _popup;
        private static float _startedAt = -1f;
        private static bool _switched;

        public static bool Playing => _startedAt >= 0f;

        /// <summary>How far into the sequence (0..1), or -1 when it isn't playing.</summary>
        public static float Progress => Playing ? Mathf.Clamp01((Time.unscaledTime - _startedAt) / Seconds) : -1f;

        public static void Play()
        {
            if (Playing) return;
            Plugin.Log.LogInfo($"Reconnect popup: shown at {ReplayTimeline.Clock(ReplayTimeline.NowMs)}");
            _startedAt = Time.unscaledTime;
            _switched = false;
            _popup = null;
            try
            {
                DialogManager.Get().ShowPopup(Info("GLOBAL_RECONNECT_RECONNECTING_HEADER", "GLOBAL_RECONNECT_RECONNECTING"),
                    (dialog, _) =>
                    {
                        _popup = dialog as AlertPopup;
                        if (!Playing && _popup != null) { _popup.Hide(); _popup = null; } // it ended before it showed
                        return true;
                    });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Reconnect popup: " + e.Message); }
        }

        /// <summary>Per frame from Plugin.Update.</summary>
        public static void Update()
        {
            if (!Playing) return;
            float t = Time.unscaledTime - _startedAt;
            if (!_switched && t >= Seconds / 2f)
            {
                _switched = true;
                Plugin.Log.LogInfo($"Reconnect popup: reconnected, at {ReplayTimeline.Clock(ReplayTimeline.NowMs)}");
                try { _popup?.UpdateInfo(Info("GLOBAL_RECONNECT_RECONNECTED_HEADER", "GLOBAL_RECONNECT_RECONNECTED")); }
                catch (Exception e) { Plugin.Log.LogWarning("Reconnect popup: " + e.Message); }
            }
            if (t >= Seconds) Stop();
        }

        public static void Stop()
        {
            if (Playing) Plugin.Log.LogInfo($"Reconnect popup: hidden at {ReplayTimeline.Clock(ReplayTimeline.NowMs)}");
            _startedAt = -1f;
            try { if (_popup != null) _popup.Hide(); }
            catch (Exception e) { Plugin.Log.LogWarning("Reconnect popup: " + e.Message); }
            _popup = null;
        }

        private static AlertPopup.PopupInfo Info(string header, string text) => new AlertPopup.PopupInfo
        {
            m_headerText = GameStrings.Get(header),
            m_text = GameStrings.Get(text),
            m_responseDisplay = AlertPopup.ResponseDisplay.NONE,
            m_showAlertIcon = true,
        };
    }
}
