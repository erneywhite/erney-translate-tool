using System;
using System.Windows;
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
    /// Update translation text and reposition over the target window.
    /// Anchors to the top-right of the target so a maximized game still
    /// keeps the overlay on screen.
    /// </summary>
    public void SetTranslation(string text, Rect targetRect, AppConfig cfg)
    {
        ApplyStyle(cfg);

        TranslationText.Text = text ?? string.Empty;
        TranslationBorder.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(text)) return;

        // Force layout pass so ActualWidth/Height are valid.
        UpdateLayout();

        const double margin = 12;
        const double titleBarOffset = 32; // skip game window title bar area

        var width = ActualWidth > 0 ? ActualWidth : 400;
        var x = targetRect.Right - width - margin;
        if (x < targetRect.Left + margin) x = targetRect.Left + margin;
        var y = targetRect.Top + titleBarOffset;

        Left = x;
        Top = y;
    }

    public void UpdateBounds(Rect windowRect)
    {
        // Re-anchor to top-right; called when target window moves/resizes.
        if (Visibility != Visibility.Visible) return;
        const double margin = 12;
        const double titleBarOffset = 32;
        var width = ActualWidth > 0 ? ActualWidth : 400;
        var x = windowRect.Right - width - margin;
        if (x < windowRect.Left + margin) x = windowRect.Left + margin;
        Left = x;
        Top = windowRect.Top + titleBarOffset;
    }

    private void ApplyStyle(AppConfig cfg)
    {
        try
        {
            var bg = (Color)ColorConverter.ConvertFromString(cfg.BackgroundColor);
            bg.A = (byte)Math.Clamp(cfg.OverlayOpacity * 255, 0, 255);
            TranslationBorder.Background = new SolidColorBrush(bg);

            var fg = (Color)ColorConverter.ConvertFromString(cfg.TextColor);
            TranslationText.Foreground = new SolidColorBrush(fg);

            if (!string.IsNullOrWhiteSpace(cfg.OverlayFontFamily))
                TranslationText.FontFamily = new FontFamily(cfg.OverlayFontFamily);

            var size = cfg.ManualFontSize >= 8 ? cfg.ManualFontSize : 16;
            TranslationText.FontSize = size;
        }
        catch
        {
            // Bad hex in settings — keep defaults.
        }
    }
}
