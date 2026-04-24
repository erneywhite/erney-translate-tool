using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using Serilog;
using WpfRect = System.Windows.Rect;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// PaddleOCR backend — significantly more accurate than Tesseract on
/// stylized / small / anti-aliased text (the hard cases for game UI),
/// at the cost of slower per-frame inference.
///
/// Initialization is fully asynchronous: the constructor never blocks the
/// caller. Loading the native Paddle runtime + downloading the language
/// model happens on a background task. Until it completes, ProcessFrame
/// returns no regions (so the rest of the pipeline stays responsive). A
/// hard 3-minute timeout means a stuck CDN can't permanently jam things.
/// </summary>
public class PaddleOcrBackend : IOcrBackend
{
    public string Name => "PaddleOCR";

    private static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(3);

    private readonly ILogger _logger;
    private PaddleOcrAll? _engine;
    private string _currentLanguage = "en";
    private volatile bool _ready;
    private volatile bool _failed;
    private CancellationTokenSource? _initCts;
    private bool _disposed;
    private readonly object _swapLock = new();

    public string CurrentLanguageTag => _currentLanguage;

    private static readonly (string Tag, string Display, OnlineFullModels Model)[] AvailableLangs =
    {
        ("en", "Английский", OnlineFullModels.EnglishV4),
        ("zh", "Китайский", OnlineFullModels.ChineseV4),
        ("ja", "Японский", OnlineFullModels.JapanV4),
        ("ko", "Корейский", OnlineFullModels.KoreanV4),
    };

    public PaddleOcrBackend(ILogger logger, string preferredLanguage)
    {
        _logger = logger;
        _currentLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? "en" : preferredLanguage;
        // Kick off the model load on a worker — never block the constructor.
        StartInit(_currentLanguage);
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() =>
        AvailableLangs.Select(l => (l.Tag, l.Display)).ToList();

    public bool SetLanguage(string tag)
    {
        var entry = AvailableLangs.FirstOrDefault(l =>
            string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (entry.Tag == null)
        {
            _logger.Warning("PaddleOCR: unsupported language {Tag}", tag);
            return false;
        }
        _currentLanguage = tag;
        StartInit(tag);
        return true;
    }

    private void StartInit(string lang)
    {
        // Cancel any in-flight init for the previous language.
        _initCts?.Cancel();
        _initCts = new CancellationTokenSource(InitTimeout);
        var ct = _initCts.Token;

        _ready = false;
        _failed = false;

        Task.Run(async () =>
        {
            try
            {
                _logger.Information("PaddleOCR: loading model {Lang} (first run downloads ~10-30 MB; subsequent runs use the local cache)...", lang);
                var entry = AvailableLangs.First(l =>
                    string.Equals(l.Tag, lang, StringComparison.OrdinalIgnoreCase));

                var model = await entry.Model.DownloadAsync().WaitAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var engine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
                {
                    AllowRotateDetection = false,
                    Enable180Classification = false,
                };

                lock (_swapLock)
                {
                    _engine?.Dispose();
                    _engine = engine;
                    _ready = true;
                }
                _logger.Information("PaddleOCR ready: {Lang}", lang);
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("PaddleOCR init for {Lang} cancelled (timeout or language switch)", lang);
                _failed = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PaddleOCR init for {Lang} failed — falling back to no-op until next attempt", lang);
                _failed = true;
            }
        });
    }

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        var regions = new List<TranslationRegion>();
        PaddleOcrAll? engine;
        lock (_swapLock) { engine = _ready ? _engine : null; }
        if (engine == null) return regions;

        try
        {
            using var src = Cv2.ImDecode(pngBytes, ImreadModes.Color);
            if (src.Empty()) return regions;

            var result = engine.Run(src);
            int kept = 0, dropped = 0;
            foreach (var r in result.Regions)
            {
                var text = r.Text?.Trim();
                if (string.IsNullOrEmpty(text)) { dropped++; continue; }
                if (OcrTextHelpers.IsEntirelyCyrillic(text)) { dropped++; continue; }

                if (r.Score < 0.6f)
                {
                    dropped++;
                    _logger.Debug("  drop[paddle-low]: score={Score:F2} '{Text}'",
                        r.Score, Truncate(text, 60));
                    continue;
                }

                var box = r.Rect.BoundingRect();
                regions.Add(new TranslationRegion
                {
                    Bounds = new WpfRect(box.X, box.Y, box.Width, box.Height),
                    OriginalText = text,
                    SourceLanguage = OcrTextHelpers.DetectLanguage(text),
                    ContainsCyrillic = OcrTextHelpers.ContainsCyrillic(text),
                    DetectedAt = DateTime.UtcNow
                });
                kept++;
                _logger.Debug("  paddle-keep: score={Score:F2} y={Y} h={H} '{Text}'",
                    r.Score, box.Y, box.Height, Truncate(text, 80));
            }
            _logger.Debug("PaddleOCR: {Total} regions, kept {Kept}, dropped {Dropped}",
                result.Regions.Length, kept, dropped);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PaddleOCR processing failed");
        }
        return regions;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _initCts?.Cancel();
        lock (_swapLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
