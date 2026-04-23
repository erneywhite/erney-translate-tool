using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ILogger _logger;
    private int _processingFlag;
    private bool _disposed;
    private long _lastFrameHash;

    public bool IsRunning { get; private set; }
    public IntPtr TargetWindowHandle { get; private set; }
    public string TargetWindowTitle { get; private set; } = string.Empty;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? StatusUpdated;

    public TranslationEngine(
        CaptureService capture,
        OcrService ocr,
        TranslationService translation,
        OverlayManager overlay,
        AppSettings settings,
        HistoryRepository history,
        ILogger logger)
    {
        _capture = capture;
        _ocr = ocr;
        _translation = translation;
        _overlay = overlay;
        _settings = settings;
        _history = history;
        _logger = logger;

        _capture.FrameCaptured += OnFrameCaptured;
    }

    public async Task StartAsync(IntPtr hwnd, string title)
    {
        if (IsRunning)
            await StopAsync();

        TargetWindowHandle = hwnd;
        TargetWindowTitle = title;

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
        StatusUpdated?.Invoke(this, $"Перевод активен: {title}");
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("Engine started for {Title}", title);
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
        if (IsRunning)
            StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
