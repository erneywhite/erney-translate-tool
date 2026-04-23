using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Views;

public partial class OverlayWindow : Window
{
    // Snap OCR-reported coordinates to this grid to kill ±1-2px jitter
    // when the same text gets re-detected frame after frame.
    private const double SnapGrid = 4.0;

    // Click-through flags.
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private string _lastFingerprint = string.Empty;
    // Sticky positions keyed by original (source) text. If OCR re-detects the
    // same English string in a spot that still overlaps the previous box, we
    // keep the previous rect instead of the freshly-jittered one.
    private Dictionary<string, Rect> _lastByOriginal = new(StringComparer.Ordinal);

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // WPF-level IsHitTestVisible only disables input inside WPF; the window
        // itself still swallows mouse events. WS_EX_TRANSPARENT makes Windows
        // route clicks straight to whatever is underneath the overlay.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            style | (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>
    /// Resize/reposition the overlay to cover the target window, then draw a
    /// translated label on top of every detected region. Positions are snapped
    /// to a grid and existing regions stick to their previous spot while OCR
    /// rediscovers them at slightly different pixels.
    /// </summary>
    public void SetRegions(IReadOnlyList<TranslationRegion> regions, Rect targetWindowRect, AppConfig cfg)
    {
        Left = targetWindowRect.Left;
        Top = targetWindowRect.Top;
        Width = Math.Max(1, targetWindowRect.Width);
        Height = Math.Max(1, targetWindowRect.Height);

        // Snap + apply sticky positioning against last frame.
        var next = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var snapped = new List<SnappedRegion>();

        foreach (var r in regions)
        {
            if (string.IsNullOrWhiteSpace(r.TranslatedText)) continue;
            if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0) continue;

            var rect = new Rect(
                Snap(r.Bounds.X),
                Snap(r.Bounds.Y),
                Snap(r.Bounds.Width),
                Snap(r.Bounds.Height));

            if (_lastByOriginal.TryGetValue(r.OriginalText, out var prev)
                && RectsOverlap(prev, rect))
            {
                // Stick to previous position — kills re-segmentation jitter.
                rect = prev;
            }

            var key = r.OriginalText;
            if (!next.ContainsKey(key)) next[key] = rect;
            snapped.Add(new SnappedRegion(rect, r.TranslatedText));
        }

        var fingerprint = string.Join("|",
            snapped.Select(s => $"{s.Rect.X}:{s.Rect.Y}:{s.Rect.Width}:{s.Rect.Height}:{s.Text}"));
        if (fingerprint == _lastFingerprint && RegionCanvas.Children.Count > 0)
        {
            _lastByOriginal = next;
            return;
        }
        _lastFingerprint = fingerprint;
        _lastByOriginal = next;

        RegionCanvas.Children.Clear();
        if (snapped.Count == 0) return;

        var bgColor = ParseColor(cfg.BackgroundColor, Colors.Black);
        bgColor.A = (byte)Math.Clamp(cfg.OverlayOpacity * 255, 60, 255);
        var bgBrush = new SolidColorBrush(bgColor);
        bgBrush.Freeze();

        var fgBrush = new SolidColorBrush(ParseColor(cfg.TextColor, Colors.White));
        fgBrush.Freeze();

        var fontFamily = !string.IsNullOrWhiteSpace(cfg.OverlayFontFamily)
            ? new FontFamily(cfg.OverlayFontFamily)
            : new FontFamily("Segoe UI");

        var manualMode = string.Equals(cfg.FontSizeMode, "Manual", StringComparison.OrdinalIgnoreCase);

        foreach (var s in snapped)
        {
            // Auto-size to roughly match source text height, but clamp to
            // a sane range so a freak OCR result spanning the whole frame
            // doesn't blow the overlay up to a wall-of-text font.
            var fontSize = manualMode && cfg.ManualFontSize >= 8
                ? cfg.ManualFontSize
                : Math.Clamp(s.Rect.Height * 0.55, 11.0, 28.0);

            var border = new Border
            {
                Background = bgBrush,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = s.Rect.Width,
                MinHeight = s.Rect.Height,
                SnapsToDevicePixels = true
            };
            border.Child = new TextBlock
            {
                Text = s.Text,
                Foreground = fgBrush,
                FontFamily = fontFamily,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(120, s.Rect.Width * 1.4),
                VerticalAlignment = VerticalAlignment.Center
            };

            Canvas.SetLeft(border, s.Rect.X);
            Canvas.SetTop(border, s.Rect.Y);
            RegionCanvas.Children.Add(border);
        }
    }

    public void UpdateBounds(Rect windowRect)
    {
        Left = windowRect.Left;
        Top = windowRect.Top;
        Width = Math.Max(1, windowRect.Width);
        Height = Math.Max(1, windowRect.Height);
    }

    private static double Snap(double v) => Math.Round(v / SnapGrid) * SnapGrid;

    private static bool RectsOverlap(Rect a, Rect b)
    {
        var ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return ix > 0 && iy > 0;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private readonly record struct SnappedRegion(Rect Rect, string Text);
}
