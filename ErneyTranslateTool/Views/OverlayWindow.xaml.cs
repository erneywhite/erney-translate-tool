using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Views;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Resize/reposition the overlay to cover the target window, then draw a
    /// translated label on top of every detected region. Each region keeps the
    /// position OCR reported (image pixels relative to the captured window).
    /// </summary>
    public void SetRegions(IReadOnlyList<TranslationRegion> regions, Rect targetWindowRect, AppConfig cfg)
    {
        // Cover the target window so absolute Canvas coords line up with OCR pixels.
        Left = targetWindowRect.Left;
        Top = targetWindowRect.Top;
        Width = Math.Max(1, targetWindowRect.Width);
        Height = Math.Max(1, targetWindowRect.Height);

        RegionCanvas.Children.Clear();
        if (regions.Count == 0) return;

        var bgColor = ParseColor(cfg.BackgroundColor, Color.FromRgb(0x1A, 0x1A, 0x1A));
        bgColor.A = (byte)Math.Clamp(cfg.OverlayOpacity * 255, 60, 255);
        var bgBrush = new SolidColorBrush(bgColor);
        bgBrush.Freeze();

        var fgBrush = new SolidColorBrush(ParseColor(cfg.TextColor, Colors.White));
        fgBrush.Freeze();

        var fontFamily = !string.IsNullOrWhiteSpace(cfg.OverlayFontFamily)
            ? new FontFamily(cfg.OverlayFontFamily)
            : new FontFamily("Segoe UI");

        var manualMode = string.Equals(cfg.FontSizeMode, "Manual", StringComparison.OrdinalIgnoreCase);

        foreach (var r in regions)
        {
            if (string.IsNullOrWhiteSpace(r.TranslatedText)) continue;
            if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0) continue;

            var fontSize = manualMode && cfg.ManualFontSize >= 8
                ? cfg.ManualFontSize
                : Math.Max(11, r.Bounds.Height * 0.7);

            var border = new Border
            {
                Background = bgBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
                SnapsToDevicePixels = true
            };
            border.Child = new TextBlock
            {
                Text = r.TranslatedText,
                Foreground = fgBrush,
                FontFamily = fontFamily,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(160, r.Bounds.Width * 1.6)
            };

            Canvas.SetLeft(border, r.Bounds.X);
            Canvas.SetTop(border, r.Bounds.Y);
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

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
}
