using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DualPCStream.Sender
{
    public static class WindowUtils
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>
        /// Window bounds in screen coordinates. Prefers the DWM extended frame
        /// bounds, which exclude the invisible resize/shadow border modern
        /// Windows adds around windows (GetWindowRect includes it, which would
        /// leave a transparent gutter around the captured image).
        /// </summary>
        public static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hWnd, out r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }
}
