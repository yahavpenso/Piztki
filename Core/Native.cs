using System.Runtime.InteropServices;
using System.Text;

namespace Piztki.Core;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);

    const uint SWP_NOZORDER = 0x0004;

    public static bool CapsLockOn => (GetKeyState(0x14) & 1) != 0;

    /// <summary>The language id of the keyboard layout in the window the user is typing in.</summary>
    public static int KeyboardLanguage
    {
        get
        {
            var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
            return (int)((long)layout & 0xFFFF);
        }
    }

    /// <summary>Centers the window horizontally at the top of the given screen area (in screen pixels).</summary>
    public static void MoveToTopCenter(IntPtr hwnd, System.Drawing.Rectangle screen)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        int width = r.Right - r.Left;
        SetWindowPos(hwnd, IntPtr.Zero, screen.Left + (screen.Width - width) / 2, screen.Top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void HideFromAltTab(IntPtr hwnd)
        => SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

    public static void KeepOnTop(IntPtr hwnd)
        => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>True when another app's window covers the whole primary screen (game, video, slides).</summary>
    public static bool IsForegroundFullscreen(IntPtr self, System.Drawing.Rectangle? onScreen = null)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        var name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

        // A maximized window also covers the screen when the taskbar auto-hides, but it is
        // not full screen: real full-screen windows are borderless and never "zoomed".
        if (IsZoomed(fg)) return false;

        if (!GetWindowRect(fg, out var r)) return false;
        // only a full-screen window on the notch's own monitor should hide it
        var screen = onScreen ?? System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
        if (screen == null) return false;
        var b = screen.Value;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}
