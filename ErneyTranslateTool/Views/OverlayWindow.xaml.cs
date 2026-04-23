using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Views;

public partial class OverlayWindow : Window
{
    // Snap OCR-reported coordinates to this grid to kill ±1-2px jitter
    // when the same text gets re-detected frame after frame.
    private const double SnapGrid = 4.0;

    private string _lastFingerprint = string.Empty;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Resize/reposition the overlay to cover the target window, then draw a
    /// translated label on top of every detected region. Positions are snapped
    /// to a grid and the whole render is skipped if the set of regions is
    /// identical to the previous frame — that prevents the visible jitter
    /// the user reported when the source text isn't actually changing.
    /// </summary>
    public void SetRegions(IReadOnlyList<TranslationRegion> regions, Rect targetWindowRect, AppConfig cfg)
    {
        Left = targetWindowRect.Left;
        Top = targetWindowRect.Top;
        Width = Math.Max(1, targetWindowRect.Width);
        Height = Math.Max(1, targetWindowRect.Height);

        // Snap + filter; build a fingerprint so we can short-circuit identical frames.
        var snapped = new List<(double X, double Y, double W, double H, string Text)>();
        foreach (var r in regions)
        {
            if (string.IsNullOrWhiteSpace(r.TranslatedText)) continue;
            if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0) continue;
            snapped.Add((
                Snap(r.Bounds.X),
                Snap(r.Bounds.Y),
                Snap(r.Bounds.Width),
                Snap(r.Bounds.Height),
                r.TranslatedText));
        }

        var fingerprint = string.Join("|",
            snapped.Select(s => $"{s.X}:{s.Y}:{s.W}:{s.H}:{s.Text}"));
        if (fingerprint == _lastFingerprint && RegionCanvas.Children.Count > 0)
            return;
        _lastFingerprint = fingerprint;

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
            var fontSize = manualMode && cfg.ManualFontSize >= 8
                ? cfg.ManualFontSize
                : Math.Max(11, s.H * 0.7);

            // Cover the original text completely: at least as big as the source rect.
            // Translation can spill out a bit horizontally if Russian needs more room.
            var border = new Border
            {
                Background = bgBrush,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = s.W,
                MinHeight = s.H,
                SnapsToDevicePixels = true
            };
            border.Child = new TextBlock
            {
                Text = s.Text,
                Foreground = fgBrush,
                FontFamily = fontFamily,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(120, s.W * 1.4),
                VerticalAlignment = VerticalAlignment.Center
            };

            Canvas.SetLeft(border, s.X);
            Canvas.SetTop(border, s.Y);
            RegionCanvas.Children.Add(border);
        }
    }

    /// <summary>Re-anchor to a moved/resized target window without re-rendering regions.</summary>
    public void UpdateBounds(Rect windowRect)
    {
        Left = windowRect.Left;
        Top = windowRect.Top;
        Width = Math.Max(1, windowRect.Width);
        Height = Math.Max(1, windowRect.Height);
    }

    private static double Snap(double v) => Math.Round(v / SnapGrid) * SnapGrid;

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
}
