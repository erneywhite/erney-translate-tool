using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ErneyTranslateTool.Core;

namespace ErneyTranslateTool.ViewModels;

/// <summary>
/// One row of the window picker on the Main tab: the raw
/// <see cref="WindowInfo"/> plus what the row draws — the app's own icon
/// from its exe, or a coloured letter tile when the icon can't be read
/// (elevated process, UWP host, exe already gone).
/// </summary>
public sealed class WindowItem
{
    // Icons are per exe, not per window: Firefox with three windows hits
    // the disk once. Lives for the app's lifetime — a few dozen frozen
    // 32 px bitmaps at most.
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    // Tile colours for the letter fallback, picked by process name so the
    // same app keeps its colour across refreshes. Dark letter on a light
    // tile — readable on every theme.
    private static readonly Color[] TileColors =
    {
        Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0xFB, 0x92, 0x3C),
        Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA7, 0x8B, 0xFA),
    };

    public WindowInfo Info { get; }
    public string Title => Info.Title;
    /// <summary>"yankee_massage.exe" when the exe path is known, otherwise the bare process name.</summary>
    public string ProcessLabel { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public string Initial { get; }
    public Brush TileBrush { get; }

    public WindowItem(WindowInfo info)
    {
        Info = info;
        var exePath = TryGetExePath(info.ProcessId);
        ProcessLabel = exePath != null ? Path.GetFileName(exePath) : info.ProcessName;
        Icon = exePath != null ? LoadIcon(exePath) : null;

        var firstLetter = info.Title.Length > 0 ? info.Title.TrimStart() : info.ProcessName;
        Initial = firstLetter.Length > 0 ? char.ToUpperInvariant(firstLetter[0]).ToString() : "?";

        var hash = 0;
        foreach (var c in info.ProcessName.ToLowerInvariant()) hash = hash * 31 + c;
        var brush = new SolidColorBrush(TileColors[(hash & int.MaxValue) % TileColors.Length]);
        brush.Freeze();
        TileBrush = brush;
    }

    private static ImageSource? LoadIcon(string exePath)
    {
        if (IconCache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                source = bmp;
            }
        }
        catch
        {
            // Unreadable exe — the letter tile takes over.
        }
        IconCache[exePath] = source;
        return source;
    }

    /// <summary>
    /// Full exe path via QueryFullProcessImageName with the limited-info
    /// access right: unlike Process.MainModule it works for most processes
    /// of other bitness and many elevated ones, and doesn't throw.
    /// </summary>
    private static string? TryGetExePath(uint processId)
    {
        if (processId == 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
