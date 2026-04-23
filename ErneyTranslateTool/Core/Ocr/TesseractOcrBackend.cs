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
            _currentLanguage = tag;
            _logger.Information("Tesseract language: {Tag}", tag);
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
            using var pix = Pix.LoadFromMemory(pngBytes);
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

                var conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                if (conf < 30) continue; // very loose — noise filtering only

                kept++;
                regions.Add(new TranslationRegion
                {
                    Bounds = new WpfRect(rect.X1, rect.Y1, rect.Width, rect.Height),
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

    public void Dispose()
    {
        if (_disposed) return;
        _engine?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
