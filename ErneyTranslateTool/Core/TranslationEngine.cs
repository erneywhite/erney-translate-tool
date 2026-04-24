using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Wires CaptureService → OcrService → TranslationService → OverlayManager
/// into a single start/stop translation pipeline.
/// </summary>
public class TranslationEngine : IDisposable
{
    private readonly CaptureService _capture;
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly OverlayManager _overlay;
    private readonly AppSettings _settings;
    private readonly HistoryRepository _history;
    private readonly ProfileManager _profiles;
    private readonly ILogger _logger;
    private int _processingFlag;
    private bool _disposed;
    private long _lastFrameHash;

    // Rolling average of "frame received → overlay updated" wall-clock time.
    // Cheap and useful as a single throughput indicator: covers OCR + grouping
    // + translation + overlay layout. Smoothed with EMA to avoid one slow
    // frame whip-sawing the readout.
    private double _avgFrameMs;
    private long _lastFrameMs;

    public bool IsRunning { get; private set; }
    public IntPtr TargetWindowHandle { get; private set; }
    public string TargetWindowTitle { get; private set; } = string.Empty;

    /// <summary>Last completed frame's end-to-end processing time in ms (0 if none yet).</summary>
    public long LastFrameMs => _lastFrameMs;

    /// <summary>Exponentially-smoothed average frame time in ms (0 if no samples yet).</summary>
    public double AverageFrameMs => _avgFrameMs;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? StatusUpdated;

    public TranslationEngine(
        CaptureService capture,
        OcrService ocr,
        TranslationService translation,
        OverlayManager overlay,
        AppSettings settings,
        HistoryRepository history,
        ProfileManager profiles,
        ILogger logger)
    {
        _capture = capture;
        _ocr = ocr;
        _translation = translation;
        _overlay = overlay;
        _settings = settings;
        _history = history;
        _profiles = profiles;
        _logger = logger;

        _capture.FrameCaptured += OnFrameCaptured;
        _capture.PauseStateChanged += OnCapturePauseChanged;
        _translation.FallbackStateChanged += OnFallbackStateChanged;
    }

    private void OnFallbackStateChanged(object? sender, string message)
    {
        // Surface the switch in the main status line so the user knows
        // why the active provider name suddenly changed in the tooltip.
        if (IsRunning) StatusUpdated?.Invoke(this, message);
    }

    private void OnCapturePauseChanged(object? sender, bool isPaused)
    {
        if (!IsRunning) return;
        // The capture loop stops feeding us frames while the window is
        // iconic, but the overlay is its own WPF window — without an
        // explicit Hide() it sticks around showing the last translation
        // until the next frame arrives. Race-y in particular at the
        // moment of minimisation: an in-flight frame can sneak through
        // and reposition the overlay to (-32000,-32000), which is why
        // a second minimise "fixed" the visual but left the bug latent.
        if (isPaused) _overlay.Hide();
        StatusUpdated?.Invoke(this, isPaused
            ? $"⏸ Окно «{TargetWindowTitle}» свёрнуто — пауза"
            : $"Перевод активен: {TargetWindowTitle}");
    }

    public async Task StartAsync(IntPtr hwnd, string title, string processName = "")
    {
        if (IsRunning)
            await StopAsync();

        TargetWindowHandle = hwnd;
        TargetWindowTitle = title;

        // Pick + apply the right profile for this window BEFORE we kick off
        // any backend so OCR/translator/overlay all read the right settings.
        // GetOrCreate auto-mints a per-process profile when nothing matched
        // and we have a sensible process name — gives the user a "settings
        // remembered per-game" experience without any UI work on their part.
        var profile = _profiles.GetOrCreateForWindow(title, processName);
        _profiles.ApplyProfile(profile);
        // Reload backends — language/engine/provider could all have changed.
        _ocr.Reload();
        _translation.Reload();

        if (!_translation.IsReady)
        {
            if (!_translation.Initialize())
            {
                StatusUpdated?.Invoke(this, "Сервис перевода не настроен — проверьте вкладку «Настройки перевода»");
                return;
            }
        }

        _settings.Config.TargetWindowHandle = hwnd;
        _settings.Config.TargetWindowTitle = title;
        _settings.Save();

        _history.StartSession(title);
        await _capture.StartCaptureAsync(hwnd);
        IsRunning = true;
        StatusUpdated?.Invoke(this,
            profile.IsDefault
                ? $"Перевод активен: {title}"
                : $"Перевод активен: {title}  ·  профиль: {profile.Name}");
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("Engine started for {Title} (profile: {Profile})", title, profile.Name);
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        await _capture.StopCaptureAsync();
        _overlay.Hide();
        _history.EndSession(
            _settings.Config.CharactersTranslatedToday,
            _settings.Config.CacheHits + _settings.Config.CacheMisses);
        IsRunning = false;
        StatusUpdated?.Invoke(this, "Перевод остановлен");
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("Engine stopped");
    }

    public void ToggleOverlay()
    {
        if (_overlay.IsVisible)
            _overlay.Hide();
        else
            _overlay.UpdatePosition(TargetWindowHandle);
    }

    private async void OnFrameCaptured(object? sender, Bitmap bitmap)
    {
        // Single-flight: drop frames if previous still being processed.
        if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
        {
            bitmap.Dispose();
            return;
        }

        var sw = Stopwatch.StartNew();
        var didWork = false;
        try
        {
            // Sample-based hash of the captured pixels: lets us skip the OCR +
            // translation pipeline entirely when the game is on a static screen.
            // Cheap (4096 pixel reads), deterministic, and good enough to
            // distinguish "still on the same dialog" from "scene changed".
            var hash = QuickSampleHash(bitmap);
            if (hash == _lastFrameHash)
            {
                bitmap.Dispose();
                return;
            }
            _lastFrameHash = hash;
            didWork = true;

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                bytes = ms.ToArray();
            }
            bitmap.Dispose();

            var rawRegions = _ocr.ProcessFrame(bytes);
            _logger.Debug("Frame: OCR -> {Count} raw regions", rawRegions.Count);
            if (rawRegions.Count == 0) return;

            // Stitch adjacent lines of the same paragraph back together so a
            // dialog that wraps to N lines is translated as one sentence
            // instead of N independent fragments.
            var regions = RegionGrouper.Group(rawRegions);
            if (regions.Count != rawRegions.Count)
                _logger.Debug("Frame: grouped {From} -> {To} regions", rawRegions.Count, regions.Count);
            foreach (var r in regions)
                _logger.Debug("  -> tx[{Y:F0},{H:F0}]: '{Text}'",
                    r.Bounds.Top, r.Bounds.Height,
                    r.OriginalText.Length > 80 ? r.OriginalText.Substring(0, 80) + "..." : r.OriginalText);

            var translated = await _translation.TranslateRegionsAsync(
                regions, _settings.Config.TargetLanguage);
            _logger.Debug("Frame: Translation -> {Count} regions", translated.Count);
            if (translated.Count == 0) return;

            if (!GetWindowRect(TargetWindowHandle, out var rect))
            {
                _logger.Warning("Frame: GetWindowRect failed for handle {Handle}", TargetWindowHandle);
                return;
            }

            var winRect = new System.Windows.Rect(
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            _overlay.ShowRegions(translated, winRect);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Engine frame processing error");
        }
        finally
        {
            // Only count frames that actually went through OCR — early-outs
            // (cache hash hit on static screen) would otherwise drag the
            // average to near-zero and hide the real cost.
            if (didWork)
            {
                _lastFrameMs = sw.ElapsedMilliseconds;
                // EMA with α=0.2 — feels responsive without being jumpy.
                _avgFrameMs = _avgFrameMs == 0
                    ? _lastFrameMs
                    : _avgFrameMs * 0.8 + _lastFrameMs * 0.2;
            }
            Interlocked.Exchange(ref _processingFlag, 0);
        }
    }

    /// <summary>
    /// Sample 64x64 grid of pixels from the bitmap and combine into a long.
    /// FNV-1a-ish; not cryptographic, just stable enough to detect "frame
    /// pixels are identical to last time".
    /// </summary>
    private static long QuickSampleHash(Bitmap bmp)
    {
        const int samples = 64;
        long hash = unchecked((long)0xcbf29ce484222325UL);
        var w = bmp.Width;
        var h = bmp.Height;
        for (int sy = 0; sy < samples; sy++)
        {
            int y = (int)((sy + 0.5) / samples * h);
            for (int sx = 0; sx < samples; sx++)
            {
                int x = (int)((sx + 0.5) / samples * w);
                hash ^= bmp.GetPixel(x, y).ToArgb();
                hash *= unchecked((long)0x100000001b3L);
            }
        }
        return hash;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public void Dispose()
    {
        if (_disposed) return;
        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.PauseStateChanged -= OnCapturePauseChanged;
        _translation.FallbackStateChanged -= OnFallbackStateChanged;
        if (IsRunning)
            StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
