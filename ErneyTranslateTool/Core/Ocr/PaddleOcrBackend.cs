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

    /// <summary>
    /// Curated PaddleOCR model catalog. We pick V5 for Chinese (newest model
    /// family available), V4 for everything that has it, V3 for Latin /
    /// Cyrillic / Traditional Chinese (only published as V3 right now).
    ///
    /// Latin/Cyrillic models cover their whole script family at once — one
    /// "latin" pick handles German, French, Spanish, Italian, Portuguese,
    /// Polish, Dutch, etc.; one "cyrillic" pick handles Russian, Ukrainian,
    /// Bulgarian, Belarusian, etc.
    /// </summary>
    private static readonly (string Tag, string Display, OnlineFullModels Model)[] AvailableLangs =
    {
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
        // Kick off the model load on a worker — never block the constructor.
        StartInit(_currentLanguage);
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() =>
        SupportedLanguages;

    /// <summary>Static accessor so the settings UI can list languages without
    /// needing a Paddle engine instance.</summary>
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

    // Cap captured-frame width sent to OCR. Paddle's detector internally
    // resizes anything > ~1280-1600 px wide to its own preferred resolution,
    // so feeding it a 4K capture mostly burns CPU on JPEG decode + Mat ops
    // without improving quality. 1600 keeps small UI fonts readable while
    // halving inference time on 1080p+ captures.
    private const int MaxOcrWidth = 1600;

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

            // Downscale very large captures before OCR. We track the scale
            // factor so we can map the bounding boxes back to original coords.
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
                var result = engine.Run(ocrMat);
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

                    // Map bounding box back to original-image coordinates
                    // when we downscaled before OCR.
                    var box = r.Rect.BoundingRect();
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
                        r.Score, bounds.Y, bounds.Height, Truncate(text, 80));
                }
                _logger.Debug("PaddleOCR: {Total} regions, kept {Kept}, dropped {Dropped} (ocr-scale={Scale:F2})",
                    result.Regions.Length, kept, dropped, scale);
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
