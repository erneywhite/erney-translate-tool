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

    // Feed Tesseract a larger image than the game renders. LSTM quality
    // improves a lot above ~30px per letter, and stylized menu fonts (thin
    // strokes, gradients, drop shadows) still benefit from extra resolution
    // even at 1080p. We used to make this conditional on source size, but
    // that silently tanked accuracy on fancy UI fonts — keeping 2x always
    // and relying on the frame-hash short-circuit for the speed side.
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
            // Auto handles both scattered labels (game menu buttons) and
            // multi-line paragraphs (dialog text wrapped over 2-3 rows).
            // SparseText found buttons fine but tended to miss the second
            // line of wrapped dialog, leaving translations partial.
            _engine.DefaultPageSegMode = PageSegMode.Auto;
            _currentLanguage = tag;
            _logger.Information("Tesseract language: {Tag} (PSM=Auto)", tag);
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

        Pix? upscaled = null;
        try
        {
            using var raw = Pix.LoadFromMemory(pngBytes);
            const float scale = UpscaleFactor;
            upscaled = raw.Scale(scale, scale);

            using var page = _engine.Process(upscaled);
            using var iter = page.GetIterator();
            iter.Begin();

            int total = 0, kept = 0, dropped = 0;
            do
            {
                total++;
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
                    continue;

                var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (OcrTextHelpers.IsEntirelyCyrillic(text))
                {
                    dropped++;
                    _logger.Debug("  drop[cyrillic-only]: '{Text}'", Truncate(text, 60));
                    continue;
                }

                var conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                if (!ShouldKeep(text, conf))
                {
                    dropped++;
                    _logger.Debug("  drop[filter]: conf={Conf:F0} '{Text}'", conf, Truncate(text, 60));
                    continue;
                }

                kept++;
                _logger.Debug("  keep: conf={Conf:F0} y={Y} h={H} '{Text}'",
                    conf, rect.Y1, rect.Height, Truncate(text, 80));
                // Bounding box is in the (possibly upscaled) coordinate space —
                // divide back so overlays land on the correct spot in the game.
                regions.Add(new TranslationRegion
                {
                    Bounds = new WpfRect(
                        rect.X1 / scale,
                        rect.Y1 / scale,
                        rect.Width / scale,
                        rect.Height / scale),
                    OriginalText = text,
                    SourceLanguage = OcrTextHelpers.DetectLanguage(text),
                    ContainsCyrillic = OcrTextHelpers.ContainsCyrillic(text),
                    DetectedAt = DateTime.UtcNow
                });
            } while (iter.Next(PageIteratorLevel.TextLine));

            _logger.Debug("Tesseract: scanned {Total}, kept {Kept}, dropped {Dropped} (scale={Scale})",
                total, kept, dropped, scale);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tesseract processing failed");
        }
        finally
        {
            upscaled?.Dispose();
        }
        return regions;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    /// <summary>
    /// Decide whether a detected line is real text or OCR noise. Combines a
    /// length-scaled confidence threshold, a structural check (must have at
    /// least 2 letters AND a contiguous letter run of 2+ — kills "&", "5",
    /// "@s", "& a 4 b" patterns), and a pattern-based "looks like garbage"
    /// check that rejects classic Tesseract misreads on textures (consonant
    /// clusters with no vowels like "MNT", capital-lower-capital triples
    /// like "AlN").
    /// </summary>
    private static bool ShouldKeep(string text, double confidence)
    {
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

        if (letters < 2 || longestRun < 2) return false;

        if (LooksLikeGarbage(text)) return false;

        double minConf = letters <= 2 ? 85
                       : letters <= 4 ? 75
                       : 65;
        return confidence >= minConf;
    }

    /// <summary>
    /// Pattern-based reject for short Tesseract hallucinations. Short text is
    /// where the noise lives — long detections are reliable. We reject:
    ///   - Short strings whose letters form a consonant-only cluster (MNT, PRT)
    ///   - Three-letter capital-lower-capital sequences (AlN, BoP) which are
    ///     almost always a single letter mis-segmented as "I → l"
    ///   - Strings that are mostly digits / punctuation with a couple of
    ///     letters dropped in
    /// </summary>
    private static bool LooksLikeGarbage(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 8) return false; // long strings get the benefit of the doubt

        var letterChars = new System.Collections.Generic.List<char>(trimmed.Length);
        foreach (var c in trimmed) if (char.IsLetter(c)) letterChars.Add(c);

        if (letterChars.Count == 0) return true;

        // No vowels in any short letter sequence — typical for texture noise.
        const string Vowels = "AEIOUYАЕИОУЫЭЮЯaeiouyаеиоуыэюя";
        if (letterChars.Count <= 5)
        {
            var hasVowel = false;
            foreach (var c in letterChars)
                if (Vowels.IndexOf(c) >= 0) { hasVowel = true; break; }
            if (!hasVowel) return true;
        }

        // Capital-lower-capital triple ("AlN", "BlR"): almost always Tesseract
        // mis-reading a single character as "l" between two upper-case letters.
        if (letterChars.Count == 3
            && char.IsUpper(letterChars[0])
            && char.IsLower(letterChars[1])
            && char.IsUpper(letterChars[2]))
            return true;

        // Mostly non-letters: e.g. "&[a4]" — barely any letters in a sea of
        // punctuation.
        if (trimmed.Length >= 4 && letterChars.Count * 2 < trimmed.Length)
            return true;

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _engine?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
