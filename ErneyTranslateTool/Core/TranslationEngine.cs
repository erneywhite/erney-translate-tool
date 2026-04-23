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
                StatusUpdated?.Invoke(this, "Не настроен ключ DeepL API");
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
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                bytes = ms.ToArray();
            }
            bitmap.Dispose();

            var regions = _ocr.ProcessFrame(bytes);
            if (regions.Count == 0)
                return;

            var translated = await _translation.TranslateRegionsAsync(
                regions, _settings.Config.TargetLanguage);

            if (translated.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var r in translated)
            {
                if (!string.IsNullOrWhiteSpace(r.TranslatedText))
                    sb.AppendLine(r.TranslatedText);
            }
            var combined = sb.ToString().Trim();
            if (string.IsNullOrEmpty(combined))
                return;

            // Anchor overlay to the top-left of the captured window.
            if (GetWindowRect(TargetWindowHandle, out var rect))
            {
                var winRect = new System.Windows.Rect(
                    rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                _overlay.ShowTranslation(combined, winRect);
            }
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
