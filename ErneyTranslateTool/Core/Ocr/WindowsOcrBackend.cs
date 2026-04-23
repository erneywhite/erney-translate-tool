using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using ErneyTranslateTool.Models;
using Serilog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// Built-in Windows.Media.OCR backend. Requires system language packs.
/// </summary>
public class WindowsOcrBackend : IOcrBackend
{
    public string Name => "WindowsOcr";

    private readonly ILogger _logger;
    private OcrEngine? _engine;
    private Language? _currentLanguage;
    private bool _disposed;

    public string CurrentLanguageTag => _currentLanguage?.LanguageTag ?? string.Empty;

    public WindowsOcrBackend(ILogger logger, string? preferredLanguage)
    {
        _logger = logger;
        InitDefault();
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
            SetLanguage(preferredLanguage);
    }

    private void InitDefault()
    {
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages;
            if (langs.Count == 0)
            {
                _logger.Warning("No Windows OCR language packs installed");
                return;
            }
            _currentLanguage = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("en")) ?? langs.First();
            _engine = OcrEngine.TryCreateFromLanguage(_currentLanguage);
            _logger.Information("WindowsOcr initialized: {Lang}", _currentLanguage.LanguageTag);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WindowsOcr init failed");
        }
    }

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList();

    public bool SetLanguage(string tag)
    {
        try
        {
            var lang = new Language(tag);
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine == null)
            {
                _logger.Warning("WindowsOcr: cannot create engine for {Tag}", tag);
                return false;
            }
            _engine = engine;
            _currentLanguage = lang;
            _logger.Information("WindowsOcr language: {Tag}", tag);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WindowsOcr SetLanguage failed for {Tag}", tag);
            return false;
        }
    }

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        var regions = new List<TranslationRegion>();
        if (_engine == null) return regions;

        try
        {
            SoftwareBitmap softwareBitmap;
            using (var stream = new InMemoryRandomAccessStream())
            {
                stream.WriteAsync(pngBytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
                stream.Seek(0);
                var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                softwareBitmap = decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            }

            using (softwareBitmap)
            {
                var result = _engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
                foreach (var line in result.Lines)
                {
                    var text = line.Text.Trim();
                    if (string.IsNullOrEmpty(text) || OcrTextHelpers.IsEntirelyCyrillic(text))
                        continue;
                    if (line.Words.Count == 0) continue;

                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = 0, maxY = 0;
                    foreach (var w in line.Words)
                    {
                        var r = w.BoundingRect;
                        if (r.X < minX) minX = r.X;
                        if (r.Y < minY) minY = r.Y;
                        if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                        if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                    }
                    regions.Add(new TranslationRegion
                    {
                        Bounds = new Rect(minX, minY, maxX - minX, maxY - minY),
                        OriginalText = text,
                        SourceLanguage = OcrTextHelpers.DetectLanguage(text),
                        ContainsCyrillic = OcrTextHelpers.ContainsCyrillic(text),
                        DetectedAt = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WindowsOcr processing failed");
        }
        return regions;
    }

    public void Dispose() { _disposed = true; GC.SuppressFinalize(this); }
}
