using System;
using System.Runtime.InteropServices;

namespace ErneyTranslateTool.Core;

/// <summary>
/// v1.0.35: the translated area is the window's client area — its content
/// without title bar, menu bar and borders. Capture crops to it and the
/// overlay is placed over it, so both agree on where (0,0) is. Before, the
/// whole window was read and the game's title and "File Edit View" menu got
/// translated too.
/// </summary>
public static class WindowGeometry
{
    /// <summary>Client area in screen pixels.</summary>
    public static bool TryGetClientRect(IntPtr hwnd, out System.Windows.Rect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var c)) return false;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        rect = new System.Windows.Rect(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top);
        return true;
    }

    /// <summary>Where the client area sits inside the window rect (what PrintWindow draws).</summary>
    public static bool TryGetClientOffset(IntPtr hwnd, out System.Drawing.Rectangle client)
    {
        client = default;
        if (!GetWindowRect(hwnd, out var w) || !TryGetClientRect(hwnd, out var c)) return false;
        client = new System.Drawing.Rectangle((int)c.X - w.Left, (int)c.Y - w.Top, (int)c.Width, (int)c.Height);
        return client.Width > 0 && client.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
