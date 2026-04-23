using System;
using System.Collections.Generic;
using System.Linq;
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
/// at the cost of a slower per-frame inference (~1-2s on 1080p) and a
/// one-time model download on first use of each language.
/// </summary>
public class PaddleOcrBackend : IOcrBackend
{
    public string Name => "PaddleOCR";

    private readonly ILogger _logger;
    private PaddleOcrAll? _engine;
    private string _currentLanguage = "en";
    private bool _disposed;

    public string CurrentLanguageTag => _currentLanguage;

    /// <summary>
    /// Curated list of language families PaddleOCR supports out of the box.
    /// Tags are intentionally short (en, ja, zh, ko, ru) — they map onto
    /// Paddle's model families, not Tesseract's three-letter codes.
    /// </summary>
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
        var initial = string.IsNullOrWhiteSpace(preferredLanguage) ? "en" : preferredLanguage;
        if (!SetLanguage(initial)) SetLanguage("en");
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() =>
        AvailableLangs.Select(l => (l.Tag, l.Display)).ToList();

    public bool SetLanguage(string tag)
    {
        try
        {
            var entry = AvailableLangs.FirstOrDefault(l =>
                string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (entry.Tag == null)
            {
                _logger.Warning("PaddleOCR: unsupported language {Tag}", tag);
                return false;
            }

            _logger.Information("PaddleOCR: loading model {Tag} (downloads ~10-20MB on first use)...", tag);
            FullOcrModel model = entry.Model.DownloadAsync().GetAwaiter().GetResult();

            _engine?.Dispose();
            _engine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = false,
                Enable180Classification = false,
            };
            _currentLanguage = tag;
            _logger.Information("PaddleOCR ready: {Tag}", tag);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PaddleOCR SetLanguage failed for {Tag}", tag);
            return false;
        }
    }

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        var regions = new List<TranslationRegion>();
        if (_engine == null) return regions;

        try
        {
            using var src = Cv2.ImDecode(pngBytes, ImreadModes.Color);
            if (src.Empty()) return regions;

            var result = _engine.Run(src);
            int kept = 0, dropped = 0;
            foreach (var r in result.Regions)
            {
                var text = r.Text?.Trim();
                if (string.IsNullOrEmpty(text)) { dropped++; continue; }
                if (OcrTextHelpers.IsEntirelyCyrillic(text)) { dropped++; continue; }

                // Paddle returns a confidence score 0-1; require at least 0.6
                // to drop the obvious junk.
                if (r.Score < 0.6f)
                {
                    dropped++;
                    _logger.Debug("  drop[paddle-low]: score={Score:F2} '{Text}'",
                        r.Score, Truncate(text, 60));
                    continue;
                }

                // r.Rect is a RotatedRect — take its axis-aligned bounding rect.
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
        _engine?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
