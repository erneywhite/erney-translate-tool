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
/// Initialization is fully asynchronous: the constructor never blocks
/// the caller. Loading the native Paddle runtime + downloading the
/// language model(s) happens on a background task. Until it completes,
/// ProcessFrame returns no regions (so the rest of the pipeline stays
/// responsive). A hard 3-minute timeout means a stuck CDN can't
/// permanently jam things.
///
/// Special "auto" language tag spins up multiple engines (English +
/// Japanese + Cyrillic by default) and merges their results per frame —
/// each region keeps the highest-confidence reading. Slower but handles
/// games whose script the user doesn't know in advance.
/// </summary>
public class PaddleOcrBackend : IOcrBackend
{
    public string Name => "PaddleOCR";

    public const string AutoTag = "auto";
    private static readonly string[] AutoBundle = { "en", "ja", "cyrillic" };

    private static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(5);
    private const int MaxOcrWidth = 1600;

    private readonly ILogger _logger;
    private readonly List<PaddleOcrAll> _engines = new();
    private string _currentLanguage = "en";
    private volatile bool _ready;
    private CancellationTokenSource? _initCts;
    private bool _disposed;
    private readonly object _swapLock = new();
    private OcrBackendState _state = OcrBackendState.NotInitialized;
    private string _statusMessage = "Не инициализирован";

    public string CurrentLanguageTag => _currentLanguage;

    public OcrBackendState State => _state;
    public string StatusMessage => _statusMessage;
    public event EventHandler? StatusChanged;

    private void SetStatus(OcrBackendState state, string message)
    {
        _state = state;
        _statusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Curated PaddleOCR model catalog. V5 for Chinese, V4 for everything
    /// that has it, V3 for Latin / Cyrillic / Traditional Chinese.
    ///
    /// Latin/Cyrillic models cover their whole script family at once — one
    /// "latin" pick handles German, French, Spanish, Italian, Portuguese,
    /// Polish, Dutch, etc.; one "cyrillic" pick handles Russian, Ukrainian,
    /// Bulgarian, Belarusian, etc.
    /// </summary>
    private static readonly (string Tag, string Display, OnlineFullModels Model)[] AvailableLangs =
    {
        (AutoTag,   "Авто (English + Japanese + Cyrillic; в 2-3 раза медленнее)", null!),
        ("en",      "Английский",                                OnlineFullModels.EnglishV4),
        ("zh",      "Китайский (упрощённый)",                    OnlineFullModels.ChineseV5),
        ("zh-tra",  "Китайский (традиционный)",                  OnlineFullModels.TraditionalChineseV3),
        ("ja",      "Японский",                                  OnlineFullModels.JapanV4),
        ("ko",      "Корейский",                                 OnlineFullModels.KoreanV4),
        ("latin",   "Латинский шрифт (DE / FR / ES / IT / PT / PL / NL / …)", OnlineFullModels.LatinV3),
        ("cyrillic","Кириллица (RU / UK / BG / BE / SR …)",      OnlineFullModels.CyrillicV3),
        ("ar",      "Арабский",                                  OnlineFullModels.ArabicV4),
        ("hi",      "Хинди / Деванагари",                        OnlineFullModels.DevanagariV4),
        ("te",      "Телугу",                                    OnlineFullModels.TeluguV4),
        ("ta",      "Тамильский",                                OnlineFullModels.TamilV4),
        ("kn",      "Каннада",                                   OnlineFullModels.KannadaV4),
    };

    public PaddleOcrBackend(ILogger logger, string preferredLanguage)
    {
        _logger = logger;
        _currentLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? "en" : preferredLanguage;
        StartInit(_currentLanguage);
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() => SupportedLanguages;

    public static List<(string Tag, string DisplayName)> SupportedLanguages =>
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
        _initCts?.Cancel();
        _initCts = new CancellationTokenSource(InitTimeout);
        var ct = _initCts.Token;
        _ready = false;

        var langsToLoad = lang == AutoTag ? AutoBundle : new[] { lang };
        SetStatus(OcrBackendState.Loading,
            langsToLoad.Length == 1
                ? $"Загрузка модели «{lang}»..."
                : $"Загрузка моделей: {string.Join(", ", langsToLoad)} (0/{langsToLoad.Length})...");

        Task.Run(async () =>
        {
            try
            {
                var loaded = new List<PaddleOcrAll>();
                int idx = 0;
                foreach (var l in langsToLoad)
                {
                    idx++;
                    SetStatus(OcrBackendState.Loading,
                        langsToLoad.Length == 1
                            ? $"Скачивание модели «{l}»..."
                            : $"Скачивание модели «{l}» ({idx}/{langsToLoad.Length})...");

                    var entry = AvailableLangs.First(e =>
                        string.Equals(e.Tag, l, StringComparison.OrdinalIgnoreCase));
                    _logger.Information("PaddleOCR: loading model {Lang}...", l);
                    var model = await entry.Model.DownloadAsync().WaitAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    SetStatus(OcrBackendState.Loading,
                        langsToLoad.Length == 1
                            ? $"Инициализация движка «{l}»..."
                            : $"Инициализация движка «{l}» ({idx}/{langsToLoad.Length})...");

                    loaded.Add(new PaddleOcrAll(model, PaddleDevice.Mkldnn())
                    {
                        AllowRotateDetection = false,
                        Enable180Classification = false,
                    });
                }

                lock (_swapLock)
                {
                    DisposeEnginesNoLock();
                    _engines.AddRange(loaded);
                    _ready = true;
                }
                _logger.Information("PaddleOCR ready: {Lang} ({Count} engine(s) active)",
                    lang, loaded.Count);
                SetStatus(OcrBackendState.Ready,
                    loaded.Count == 1
                        ? $"Готов: {lang}"
                        : $"Готов: {lang} ({loaded.Count} движков)");
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("PaddleOCR init for {Lang} cancelled (timeout or language switch)", lang);
                SetStatus(OcrBackendState.Failed, $"Загрузка отменена (таймаут {InitTimeout.TotalMinutes:F0} мин или переключение языка)");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PaddleOCR init for {Lang} failed", lang);
                SetStatus(OcrBackendState.Failed, $"Ошибка: {ex.Message}");
            }
        });
    }

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        var regions = new List<TranslationRegion>();
        PaddleOcrAll[] engines;
        lock (_swapLock) { engines = _ready ? _engines.ToArray() : Array.Empty<PaddleOcrAll>(); }
        if (engines.Length == 0) return regions;

        try
        {
            using var src = Cv2.ImDecode(pngBytes, ImreadModes.Color);
            if (src.Empty()) return regions;

            // Downscale very large captures before OCR.
            double scale = 1.0;
            Mat ocrMat = src;
            Mat? scaledMat = null;
            if (src.Width > MaxOcrWidth)
            {
                scale = (double)MaxOcrWidth / src.Width;
                scaledMat = new Mat();
                Cv2.Resize(src, scaledMat,
                    new Size((int)(src.Width * scale), (int)(src.Height * scale)),
                    interpolation: InterpolationFlags.Area);
                ocrMat = scaledMat;
            }

            try
            {
                // Collect detections from every active engine.
                var raw = new List<(Rect Box, string Text, float Score)>();
                foreach (var engine in engines)
                {
                    var result = engine.Run(ocrMat);
                    foreach (var r in result.Regions)
                    {
                        var text = r.Text?.Trim();
                        if (string.IsNullOrEmpty(text)) continue;
                        if (OcrTextHelpers.IsEntirelyCyrillic(text) && _currentLanguage != "cyrillic" && _currentLanguage != AutoTag) continue;
                        if (r.Score < 0.6f) continue;

                        var box = r.Rect.BoundingRect();
                        raw.Add((box, text, r.Score));
                    }
                }

                // Deduplicate overlapping detections (multi-engine mode produces
                // duplicates: two engines see the same region and both emit it).
                // For each cluster of overlapping boxes, keep the one with the
                // highest score.
                var winners = DeduplicateByOverlap(raw);

                int kept = 0;
                foreach (var (box, text, score) in winners)
                {
                    var bounds = scale == 1.0
                        ? new WpfRect(box.X, box.Y, box.Width, box.Height)
                        : new WpfRect(box.X / scale, box.Y / scale,
                                      box.Width / scale, box.Height / scale);

                    regions.Add(new TranslationRegion
                    {
                        Bounds = bounds,
                        OriginalText = text,
                        SourceLanguage = OcrTextHelpers.DetectLanguage(text),
                        ContainsCyrillic = OcrTextHelpers.ContainsCyrillic(text),
                        DetectedAt = DateTime.UtcNow
                    });
                    kept++;
                    _logger.Debug("  paddle-keep: score={Score:F2} y={Y:F0} h={H:F0} '{Text}'",
                        score, bounds.Y, bounds.Height, Truncate(text, 80));
                }
                _logger.Debug("PaddleOCR: {Engines} engine(s), {Raw} raw, kept {Kept} (ocr-scale={Scale:F2})",
                    engines.Length, raw.Count, kept, scale);
            }
            finally
            {
                scaledMat?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PaddleOCR processing failed");
        }
        return regions;
    }

    /// <summary>
    /// Greedy IoU dedup: walk results from highest score down, accept each
    /// only if it doesn't significantly overlap an already-accepted region.
    /// </summary>
    private static List<(Rect Box, string Text, float Score)> DeduplicateByOverlap(
        List<(Rect Box, string Text, float Score)> input)
    {
        const double iouThreshold = 0.4;
        var sorted = input.OrderByDescending(r => r.Score).ToList();
        var winners = new List<(Rect Box, string Text, float Score)>();
        foreach (var candidate in sorted)
        {
            bool overlapsExisting = winners.Any(w => Iou(w.Box, candidate.Box) > iouThreshold);
            if (!overlapsExisting) winners.Add(candidate);
        }
        return winners;
    }

    private static double Iou(Rect a, Rect b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x2 <= x1 || y2 <= y1) return 0;
        double inter = (double)(x2 - x1) * (y2 - y1);
        double areaA = (double)a.Width * a.Height;
        double areaB = (double)b.Width * b.Height;
        double union = areaA + areaB - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    private void DisposeEnginesNoLock()
    {
        foreach (var e in _engines) e.Dispose();
        _engines.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _initCts?.Cancel();
        lock (_swapLock) { DisposeEnginesNoLock(); }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
