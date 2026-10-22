using System;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The game's alert popup asking before a destructive action, its buttons in their own roles: the left one
    /// (the green check) is the action, its label green; the right one (the red X) is Cancel, its label red, and
    /// Esc presses it. The labels are the buttons' own UberTexts, coloured.
    /// </summary>
    internal static class ConfirmPopup
    {
        private static readonly Color Green = new Color(0.1f, 0.42f, 0.08f), Red = new Color(0.62f, 0.08f, 0.06f);

        public static void Ask(string header, string text, string action, Action onAction, Action onCancel = null)
        {
            var info = new AlertPopup.PopupInfo
            {
                m_headerText = header,
                m_text = text,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_iconSet = AlertPopup.PopupInfo.IconSet.Default,
                m_confirmText = action,
                m_cancelText = GameStrings.Get("GLOBAL_CANCEL"),
                m_keyboardEscIsCancel = true,
                m_responseCallback = (response, _) =>
                {
                    if (response == AlertPopup.Response.CONFIRM) onAction?.Invoke();
                    else onCancel?.Invoke();
                },
            };
            DialogManager.Get().ShowPopup(info, (dialog, _) =>
            {
                var popup = dialog as AlertPopup;
                if (popup != null)
                {
                    Colour(popup.m_confirmButton, Green);
                    Colour(popup.m_cancelButton, Red);
                }
                return true;
            });
        }

        private static void Colour(UIBButton button, Color colour)
        {
            if (button == null) return;
            foreach (var t in button.GetComponentsInChildren<UberText>(true)) t.TextColor = colour;
        }
    }
}
