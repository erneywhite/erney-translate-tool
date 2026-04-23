using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ErneyTranslateTool.Models;
using Serilog;
using Tesseract;
using WpfRect = System.Windows.Rect;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// Tesseract OCR backend. Reads .traineddata files from the user's tessdata
/// directory, no system language packs required. Supports multi-language
/// recognition by passing "eng+jpn" etc.
/// </summary>
public class TesseractOcrBackend : IOcrBackend
{
    public string Name => "Tesseract";

    // Feed Tesseract a larger image than the game renders — LSTM quality
    // improves substantially above ~30px per letter. 2x is a good balance
    // between quality and CPU cost.
    private const float UpscaleFactor = 2.0f;

    private readonly TessdataManager _tessdata;
    private readonly ILogger _logger;
    private TesseractEngine? _engine;
    private string _currentLanguage = "eng";
    private bool _disposed;

    public string CurrentLanguageTag => _currentLanguage;

    public TesseractOcrBackend(TessdataManager tessdata, ILogger logger, string preferredLanguage)
    {
        _tessdata = tessdata;
        _logger = logger;

        var initial = !string.IsNullOrWhiteSpace(preferredLanguage) ? preferredLanguage : "eng";
        if (!SetLanguage(initial))
        {
            // Fall back to whatever we have installed.
            var installed = _tessdata.GetInstalledLanguageCodes().FirstOrDefault();
            if (installed != null) SetLanguage(installed);
        }
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages()
    {
        return _tessdata.GetInstalledLanguageCodes()
            .Select(code => (code, TesseractLanguages.DisplayNameFor(code)))
            .ToList();
    }

    public bool SetLanguage(string tag)
    {
        try
        {
            var codes = tag.Split('+', StringSplitOptions.RemoveEmptyEntries);
            foreach (var c in codes)
            {
                if (!_tessdata.IsInstalled(c))
                {
                    _logger.Warning("Tesseract: language not installed: {Code}", c);
                    return false;
                }
            }

            _engine?.Dispose();
            _engine = new TesseractEngine(_tessdata.TessdataPath, tag, EngineMode.Default);
            // SparseText works far better than the default PSM for game UIs —
            // it doesn't assume a newspaper-style layout and finds scattered
            // labels and button text reliably.
            _engine.DefaultPageSegMode = PageSegMode.SparseText;
            _currentLanguage = tag;
            _logger.Information("Tesseract language: {Tag} (PSM=SparseText)", tag);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tesseract SetLanguage failed for {Tag}", tag);
            return false;
        }
    }

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        var regions = new List<TranslationRegion>();
        if (_engine == null)
        {
            _logger.Debug("Tesseract: engine is null, skipping frame");
            return regions;
        }

        try
        {
            using var raw = Pix.LoadFromMemory(pngBytes);
            // Upscale before OCR — gives Tesseract more pixels per letter,
            // substantially improves accuracy on pixel-art / stylized fonts.
            using var pix = raw.Scale(UpscaleFactor, UpscaleFactor);
            using var page = _engine.Process(pix);
            using var iter = page.GetIterator();
            iter.Begin();

            int total = 0, kept = 0;
            do
            {
                total++;
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
                    continue;

                var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (OcrTextHelpers.IsEntirelyCyrillic(text)) continue;

                // Filter out OCR noise: Tesseract loves to hallucinate on
                // textures, decorative borders, jewelry etc. and emit short
                // garbage strings ("Дж", "&", "от В до е 4"...) that
                // translators happily turn into nonsense.
                if (!IsLikelyText(text)) continue;

                var conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                if (conf < 65) continue;

                kept++;
                // Bounding box is in the upscaled coordinate space — divide
                // back so overlays land on the correct spot in the game window.
                regions.Add(new TranslationRegion
                {
                    Bounds = new WpfRect(
                        rect.X1 / UpscaleFactor,
                        rect.Y1 / UpscaleFactor,
                        rect.Width / UpscaleFactor,
                        rect.Height / UpscaleFactor),
                    OriginalText = text,
                    SourceLanguage = OcrTextHelpers.DetectLanguage(text),
                    ContainsCyrillic = OcrTextHelpers.ContainsCyrillic(text),
                    DetectedAt = DateTime.UtcNow
                });
            } while (iter.Next(PageIteratorLevel.TextLine));

            _logger.Debug("Tesseract: scanned {Total} lines, kept {Kept}", total, kept);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tesseract processing failed");
        }
        return regions;
    }

    /// <summary>
    /// Reject single-letter and mostly-symbol detections, plus strings whose
    /// longest run of letters is too short to plausibly be a real word.
    /// </summary>
    private static bool IsLikelyText(string text)
    {
        // Need at least three letters total.
        int letters = 0, longestRun = 0, currentRun = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
                currentRun++;
                if (currentRun > longestRun) longestRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }
        if (letters < 3) return false;
        // Require at least one contiguous letter run of length 3+ (filters
        // things like "от В до е 4" — letters but not real words).
        if (longestRun < 3) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _engine?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
