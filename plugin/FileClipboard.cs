using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Files on the Windows clipboard, as Explorer's Copy puts them there: CF_HDROP (a DROPFILES header, then the
    /// paths in UTF-16, each ended by a 0, the list by another) with "Preferred DropEffect" = copy, so chat apps paste
    /// the file itself (Ctrl+V attaches it). Called on the game's main thread.
    /// </summary>
    internal static class FileClipboard
    {
        private const uint CF_HDROP = 15, GMEM_MOVEABLE = 2, DROPEFFECT_COPY = 1;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

        /// <summary>Put the files on the clipboard (false: it stayed busy, or Windows refused).</summary>
        public static bool SetFiles(IEnumerable<string> paths)
        {
            var text = new StringBuilder();
            foreach (string p in paths) text.Append(p).Append('\0');
            text.Append('\0');
            byte[] names = Encoding.Unicode.GetBytes(text.ToString());
            var drop = new byte[20 + names.Length];
            BitConverter.GetBytes(20).CopyTo(drop, 0); // pFiles: the paths start after the header
            BitConverter.GetBytes(1).CopyTo(drop, 16); // fWide: UTF-16 (pt and fNC stay 0)
            names.CopyTo(drop, 20);
            uint effectFormat = RegisterClipboardFormat("Preferred DropEffect");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(30); continue; }
                try
                {
                    EmptyClipboard();
                    if (!Put(CF_HDROP, drop)) return false;
                    if (effectFormat != 0) Put(effectFormat, BitConverter.GetBytes(DROPEFFECT_COPY));
                    return true;
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

        private static bool Put(uint format, byte[] data)
        {
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (mem == IntPtr.Zero) return false;
            var ptr = GlobalLock(mem);
            Marshal.Copy(data, 0, ptr, data.Length);
            GlobalUnlock(mem);
            if (SetClipboardData(format, mem) != IntPtr.Zero) return true; // the clipboard owns it now
            GlobalFree(mem);
            return false;
        }
    }
}
