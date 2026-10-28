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
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    private const int GWLP_HWNDPARENT = -8;
    private const uint GW_OWNER = 4;

    private IntPtr _owner;

    /// <summary>
    /// v1.0.35: make the overlay an owned window of the translated one.
    /// Windows keeps an owned window right above its owner, so a window
    /// dragged over the game now covers the translation too (before, the
    /// overlay was Topmost and showed through everything), and it hides
    /// with the game when that is minimised. If ownership can't be set —
    /// e.g. the game runs elevated — stay Topmost as before, so the
    /// translation never ends up under the game.
    /// </summary>
    public void AttachTo(IntPtr target)
    {
        if (target == _owner) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, target);
        var attached = target != IntPtr.Zero && GetWindow(hwnd, GW_OWNER) == target;
        Topmost = !attached;
        _owner = attached ? target : IntPtr.Zero;
    }

    private string _lastFingerprint = string.Empty;
    private string _lastStyleFingerprint = string.Empty;
    // Sticky positions keyed by original (source) text. If OCR re-detects the
    // same English string in a spot that still overlaps the previous box, we
    // keep the previous rect instead of the freshly-jittered one.
    private Dictionary<string, Rect> _lastByOriginal = new(StringComparer.Ordinal);
    // Active borders on the canvas, keyed by ORIGINAL text. Originally keyed
    // by translated text, but v1.0.15's streaming LLM path re-renders the
    // same region with progressively longer translated strings ("Доб" →
    // "Добро" → "Добро по...") — keying by the unstable translated value
    // would create + destroy a Border per chunk and flicker visibly.
    // Keying by the stable OriginalText lets us update the inner TextBlock
    // in place as chunks arrive.
    private readonly Dictionary<string, Border> _activeBorders = new(StringComparer.Ordinal);

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
    ///
    /// <para>v1.0.30: <paramref name="isPartial"/> = true for in-progress
    /// updates. The list then holds only the regions translated so far, so
    /// a label missing from it is "not reached yet", not "gone": we keep it
    /// unless a new label overlaps it (that one is being replaced, e.g. a
    /// merged paragraph re-translated as two lines). Before this every
    /// streaming chunk wiped the labels further down the queue and the final
    /// render re-created them — visible flicker on every network call.</para>
    /// </summary>
    public void SetRegions(IReadOnlyList<TranslationRegion> regions, Rect targetWindowRect, AppConfig cfg, bool isPartial = false)
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
            snapped.Add(new SnappedRegion(rect, r.OriginalText, r.TranslatedText));
        }

        if (isPartial)
        {
            // Nothing translated yet — leave the canvas exactly as it is.
            if (snapped.Count == 0) return;
            // Keep sticky positions of labels this partial list doesn't
            // cover yet; the final render will settle them.
            foreach (var (k, v) in _lastByOriginal)
                next.TryAdd(k, v);
        }

        // The mode is part of the fingerprint: the last partial update of a
        // frame often carries exactly the final region list, and if the
        // final render short-circuited on it, labels of text that left the
        // screen would never be dropped (partial updates don't drop them).
        var fingerprint = (isPartial ? "P|" : "F|") + string.Join("|",
            snapped.Select(s => $"{s.Rect.X}:{s.Rect.Y}:{s.Rect.Width}:{s.Rect.Height}:{s.Text}"));
        var styleFingerprint = $"{cfg.BackgroundColor}|{cfg.TextColor}|{cfg.OverlayOpacity:F3}|" +
            $"{cfg.OverlayCornerRadius:F0}|{cfg.OverlayFontFamily}|{cfg.ManualFontSize:F0}";

        if (fingerprint == _lastFingerprint
            && styleFingerprint == _lastStyleFingerprint
            && RegionCanvas.Children.Count > 0)
        {
            _lastByOriginal = next;
            return;
        }

        // If only style changed (theme switch / colour tweak), wipe and
        // rebuild — every Border has its old brushes baked in.
        if (styleFingerprint != _lastStyleFingerprint)
        {
            RegionCanvas.Children.Clear();
            _activeBorders.Clear();
        }
        _lastFingerprint = fingerprint;
        _lastStyleFingerprint = styleFingerprint;
        _lastByOriginal = next;

        if (snapped.Count == 0)
        {
            RegionCanvas.Children.Clear();
            _activeBorders.Clear();
            return;
        }

        var bgColor = ParseColor(cfg.BackgroundColor, Colors.Black);
        bgColor.A = (byte)Math.Clamp(cfg.OverlayOpacity * 255, 60, 255);
        var bgBrush = new SolidColorBrush(bgColor);
        bgBrush.Freeze();

        var fgBrush = new SolidColorBrush(ParseColor(cfg.TextColor, Colors.White));
        fgBrush.Freeze();

        var fontFamily = !string.IsNullOrWhiteSpace(cfg.OverlayFontFamily)
            ? new FontFamily(cfg.OverlayFontFamily)
            : new FontFamily("Segoe UI");

        // Always use the user's chosen font size from the Overlay Settings
        // tab. Earlier versions auto-scaled to the source rect's height,
        // which made labels jitter between sizes from frame to frame even
        // when the original text wasn't changing.
        var fontSize = cfg.ManualFontSize >= 8 ? cfg.ManualFontSize : 16.0;
        var cornerRadius = new CornerRadius(Math.Clamp(cfg.OverlayCornerRadius, 0, 16));

        // Diff-based update. For each region in the new frame: if a Border
        // for that source text exists, reposition it AND update the inner
        // TextBlock.Text in place (the streaming-LLM hot path). Otherwise
        // create a new Border. Tracked via _activeBorders keyed by Original
        // so we can drop borders that disappear in a later frame.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in snapped)
        {
            const double rightMargin = 12;
            // v1.0.21: tighter cap. The v1.0.20 formula
            // (source × 1.3 × fontScale + 60 × fontScale) double-counted
            // the font scale — at 21 pt it allowed ~1.95× source width,
            // which on a wide dialog (1000+ px) translated to a 400+ px
            // overshoot past the source's right edge. Visually broken.
            //
            // New formula: a small fixed proportional bump (×1.1) plus
            // an additive font-aware buffer (~80 × fontScale). For wide
            // sources the multiplicative term dominates and stays close
            // to the source width; for narrow sources (single short
            // labels) the additive term ensures a long Russian word can
            // still fit at large font sizes without clipping.
            var hardLimit = Math.Max(60, Width - s.Rect.X - rightMargin);
            var fontScale = Math.Max(1.0, fontSize / 14.0);
            var sourceCap = s.Rect.Width * 1.1 + 80 * fontScale;
            var availableWidth = Math.Min(hardLimit, sourceCap);
            // But never less than the source — translation must at least
            // cover the original text underneath, otherwise it bleeds
            // through visibly.
            availableWidth = Math.Max(availableWidth, Math.Min(s.Rect.Width, hardLimit));

            keep.Add(s.Original);
            if (_activeBorders.TryGetValue(s.Original, out var existing))
            {
                // Reposition / resize existing border in place.
                Canvas.SetLeft(existing, s.Rect.X);
                Canvas.SetTop(existing, s.Rect.Y);
                existing.MinWidth = Math.Min(s.Rect.Width, availableWidth);
                existing.MinHeight = s.Rect.Height;
                existing.MaxWidth = availableWidth;
                if (existing.Child is TextBlock tb)
                {
                    tb.MaxWidth = availableWidth - 8;
                    // In-place text update is what makes streaming feel
                    // smooth — no Border destroy/create churn between
                    // chunks, the user sees the existing label grow.
                    if (!ReferenceEquals(tb.Text, s.Text) && tb.Text != s.Text)
                        tb.Text = s.Text;
                }
                continue;
            }

            // Border has to cover the original English text (so the user
            // doesn't see it bleeding through under the translation), so
            // MinWidth/MinHeight equal the source rect. If the Russian
            // translation needs more room than that, WPF lets the Border
            // grow up to MaxWidth — anything bigger and the text wraps to
            // a new line and the Border grows vertically instead.
            var border = new Border
            {
                Background = bgBrush,
                CornerRadius = cornerRadius,
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = Math.Min(s.Rect.Width, availableWidth),
                MinHeight = s.Rect.Height,
                MaxWidth = availableWidth,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            border.Child = new TextBlock
            {
                Text = s.Text,
                Foreground = fgBrush,
                FontFamily = fontFamily,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = availableWidth - 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            Canvas.SetLeft(border, s.Rect.X);
            Canvas.SetTop(border, s.Rect.Y);
            RegionCanvas.Children.Add(border);
            _activeBorders[s.Original] = border;
        }

        // Drop borders whose source text no longer appears in this frame.
        // Partial update: only the ones a new label is drawn over.
        if (_activeBorders.Count != keep.Count)
        {
            var toRemove = _activeBorders
                .Where(kv => !keep.Contains(kv.Key)
                             && (!isPartial || snapped.Any(s => RectsOverlap(BorderRect(kv.Value), s.Rect))))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toRemove)
            {
                if (_activeBorders.TryGetValue(k, out var b))
                    RegionCanvas.Children.Remove(b);
                _activeBorders.Remove(k);
            }
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

    /// <summary>Current on-canvas rect of a label; falls back to its min size before the first layout pass.</summary>
    private static Rect BorderRect(Border b) => new(
        Canvas.GetLeft(b),
        Canvas.GetTop(b),
        b.ActualWidth > 0 ? b.ActualWidth : b.MinWidth,
        b.ActualHeight > 0 ? b.ActualHeight : b.MinHeight);

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

    /// <summary>
    /// Original = stable identity (same OCR'd source text across frames and
    /// streaming chunks). Text = the current translation to display, which
    /// can grow chunk-by-chunk during a streaming LLM call.
    /// </summary>
    private readonly record struct SnappedRegion(Rect Rect, string Original, string Text);
}
