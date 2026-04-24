using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ErneyTranslateTool.Core.Tray;

/// <summary>
/// State of the tray icon — selects which colored "status dot" is overlaid
/// on the base app icon. Slack/Discord pattern: one glance tells you whether
/// the app is doing something useful, idle, needs attention, or errored.
/// </summary>
public enum TrayIconState
{
    /// <summary>No active translation, nothing pending — gray dot (default).</summary>
    Idle,
    /// <summary>Engine actively translating — green dot.</summary>
    Translating,
    /// <summary>Something needs the user (e.g. update available) — amber dot.</summary>
    Attention,
    /// <summary>Recoverable error happened — red dot.</summary>
    Error,
}

/// <summary>
/// Renders the tray icon by overlaying a colored circle on the base app icon.
/// Renderings are cached per-state so we only do the GDI work once per
/// process lifetime.
/// </summary>
public static class TrayIconRenderer
{
    // Lazy per-state cache. Bitmaps + Icons are cleaned up when the process
    // exits — they're tiny (~32x32 ARGB) so leaking them isn't a concern.
    private static readonly System.Windows.Media.ImageSource?[] _cache =
        new System.Windows.Media.ImageSource?[Enum.GetValues(typeof(TrayIconState)).Length];

    /// <summary>
    /// Returns the cached <see cref="System.Windows.Media.ImageSource"/> for
    /// the given state, building it on first request. Always returns null
    /// if the base icon can't be loaded — callers should keep using
    /// whatever they already had.
    /// </summary>
    public static System.Windows.Media.ImageSource? GetIconFor(TrayIconState state)
    {
        var i = (int)state;
        if (_cache[i] != null) return _cache[i];

        try
        {
            using var baseBmp = LoadBaseBitmap(32);
            if (baseBmp == null) return null;

            using var composed = ComposeWithDot(baseBmp, state);
            var bs = ConvertToBitmapSource(composed);
            _cache[i] = bs;
            return bs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Load the embedded app.ico into a 32-bit ARGB Bitmap of the requested
    /// size. The .ico ships with multiple sizes — System.Drawing picks the
    /// closest match.
    /// </summary>
    private static Bitmap? LoadBaseBitmap(int size)
    {
        // Try the resource pack URI first (works once WPF Application is
        // alive); fall back to walking the on-disk Resources folder for the
        // (rare) case we're called pre-Application.
        Stream? stream = null;
        try
        {
            var sri = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/Icons/app.ico", UriKind.Absolute));
            stream = sri?.Stream;
        }
        catch
        {
            stream = null;
        }

        if (stream == null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "app.ico");
            if (File.Exists(path)) stream = File.OpenRead(path);
        }

        if (stream == null) return null;

        using (stream)
        using (var icon = new Icon(stream, size, size))
        {
            return icon.ToBitmap();
        }
    }

    /// <summary>
    /// Paint a colored dot in the bottom-right corner of a copy of the base
    /// bitmap. Dot diameter is ~38 % of the icon — large enough to be
    /// readable at the standard 16x16 tray render, small enough that the
    /// app glyph stays recognisable.
    /// </summary>
    private static Bitmap ComposeWithDot(Bitmap baseBmp, TrayIconState state)
    {
        var w = baseBmp.Width;
        var h = baseBmp.Height;

        var result = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(baseBmp, 0, 0, w, h);

            // Idle is the "no badge" state — return the bare icon.
            if (state == TrayIconState.Idle) return result;

            var dotColor = state switch
            {
                TrayIconState.Translating => Color.FromArgb(0xFF, 0x10, 0xB9, 0x81), // green
                TrayIconState.Attention   => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), // amber
                TrayIconState.Error       => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), // red
                _                         => Color.Gray,
            };

            // Diameter ~38 % so it's punchy at 16x16 (≈6 px) and at 32x32 (≈12 px).
            var d = (int)Math.Round(w * 0.38);
            // Bottom-right corner; leave a 1-px gap so the dot doesn't
            // touch the icon edge (looks crisper).
            var x = w - d - 1;
            var y = h - d - 1;

            // White outline so the dot stays visible regardless of the
            // taskbar colour (dark by default, but users can change it).
            using (var outline = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)))
                g.FillEllipse(outline, x - 1, y - 1, d + 2, d + 2);
            using (var fill = new SolidBrush(dotColor))
                g.FillEllipse(fill, x, y, d, d);
        }
        return result;
    }

    /// <summary>
    /// Marshal a System.Drawing Bitmap to a WPF BitmapSource that can be
    /// assigned to TaskbarIcon.IconSource. Freezes for thread safety.
    /// </summary>
    private static System.Windows.Media.ImageSource ConvertToBitmapSource(Bitmap bmp)
    {
        var hBitmap = bmp.GetHbitmap();
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(bmp.Width, bmp.Height));
            bs.Freeze();
            return bs;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
