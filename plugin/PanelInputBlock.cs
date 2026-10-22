using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Keeps clicks on the replay bar (and the Replays page's layout editor) away from the board.
    ///
    /// IMGUI gets its clicks from Event.current, but the game reads the mouse separately: InputManager (cards,
    /// board) and PegUI (buttons) both ask the static InputCollection, which returns the answer of the first
    /// IInput that reports a call as handled (the stock list holds only UnityInput). This IInput goes first.
    /// While the pointer is over the panel it answers mouse-button queries itself with "not pressed"; a button
    /// pressed on the panel stays hidden until it is released, wherever that happens. A press that started on
    /// the board is left alone, so a card dragged over the panel and released there still drops.
    /// </summary>
    internal sealed class PanelInputBlock : IInput
    {
        /// <summary>The panel's area in Input.mousePosition coordinates (pixels, y up); empty when hidden.</summary>
        public static Rect ScreenRect;

        /// <summary>Also swallow a press while this says so (the layout editor grabbing a card element).</summary>
        public static System.Func<bool> Grabs;

        /// <summary>The pointer is over the panel itself.</summary>
        public static bool OverEditorPanel() => ScreenRect.width > 0 && ScreenRect.Contains(Input.mousePosition);

        private static PanelInputBlock _instance;
        private readonly bool[] _swallowed = new bool[3];
        private readonly int[] _releasedFrame = { -1, -1, -1 }; // the release stays hidden for every caller that frame

        public static void Install()
        {
            if (_instance != null) return;
            var inputs = AccessTools.StaticFieldRefAccess<List<IInput>>(typeof(InputCollection), "m_Inputs");
            if (inputs == null)
            {
                Plugin.Log.LogWarning("Panel input block: InputCollection.m_Inputs not found; clicks on the panel reach the board");
                return;
            }
            _instance = new PanelInputBlock();
            inputs.Insert(0, _instance);
        }

        private static bool OverPanel() => OverEditorPanel() || (Grabs != null && Grabs());

        private bool Swallowed(int button) =>
            button >= 0 && button < _swallowed.Length && (_swallowed[button] || _releasedFrame[button] == Time.frameCount);

        public bool GetMouseButtonDown(int button, out bool value)
        {
            value = false;
            if (Swallowed(button)) return true;
            if (button < 0 || button >= _swallowed.Length || !Input.GetMouseButtonDown(button) || !OverPanel()) return false;
            _swallowed[button] = true;
            return true;
        }

        public bool GetMouseButton(int button, out bool value)
        {
            value = false;
            return Swallowed(button);
        }

        public bool GetMouseButtonUp(int button, out bool value)
        {
            value = false;
            if (!Swallowed(button)) return false;
            if (_swallowed[button] && (Input.GetMouseButtonUp(button) || !Input.GetMouseButton(button)))
            {
                _swallowed[button] = false;
                _releasedFrame[button] = Time.frameCount;
            }
            return true;
        }

        // Pointer position and keys stay with UnityInput.
        public bool GetMousePosition(out Vector3 position) { position = Vector3.zero; return false; }
        public bool GetAnyKey(out bool value) { value = false; return false; }
        public bool GetKey(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyDown(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyUp(KeyCode keycode, out bool value) { value = false; return false; }
    }
}
