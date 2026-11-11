using System;
using System.Runtime.InteropServices;

namespace WaveDock
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong32(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x00000080;
        const long WS_EX_NOACTIVATE = 0x08000000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        public static bool TryGetTaskbarRect(out RECT rect)
        {
            rect = default;
            var h = FindWindow("Shell_TrayWnd", null);
            return h != IntPtr.Zero && GetWindowRect(h, out rect);
        }

        public static void AddExStyle(IntPtr hwnd, bool noActivate)
        {
            long style = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);
            style |= WS_EX_TOOLWINDOW;
            if (noActivate) style |= WS_EX_NOACTIVATE;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)style);
        }

        public static void KeepTopmost(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        public static bool LeftButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        /// <summary>
        /// Игра/видео на весь экран — виджет лучше спрятать.
        /// Смотрим только на АКТИВНОЕ окно: прозрачные оверлеи (NVIDIA, запись экрана и т.п.) не мешают.
        /// </summary>
        public static bool IsFullscreenAppRunning()
        {
            try
            {
                if (SHQueryUserNotificationState(out int st) == 0 && (st == 3 || st == 4)) return true; // D3D-игра, презентация

                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;

                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)Environment.ProcessId) return false;

                var cls = new System.Text.StringBuilder(64);
                GetClassName(fg, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;

                if (!GetWindowRect(fg, out var r)) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
                var m = mi.rcMonitor;
                return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            }
            catch { return false; }
        }

        public static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
}
