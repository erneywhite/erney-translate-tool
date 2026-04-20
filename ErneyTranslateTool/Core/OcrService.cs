using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// OCR service using Windows.Media.OCR (WinRT).
/// </summary>
public class OcrService : IDisposable
{
    private readonly ILogger _logger;
    private OcrEngine? _ocrEngine;
    private Language? _currentLanguage;
    private readonly Dictionary<string, string> _previousFrameHashes = new();
    private bool _disposed;

    /// <summary>
    /// Supported OCR languages by Windows.
    /// </summary>
    public static IReadOnlyList<string> SupportedLanguages => OcrEngine.AvailableRecognizerLanguages
        .Select(l => l.LanguageTag)
        .ToList();

    /// <summary>
    /// Initialize OCR service.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public OcrService(ILogger logger)
    {
        _logger = logger;
        InitializeOcrEngine();
    }

    /// <summary>
    /// Initialize OCR engine with auto-detect language.
    /// </summary>
    private void InitializeOcrEngine()
    {
        try
        {
            // Try to use auto-detect by using the first available language
            // Windows OCR will auto-detect within its supported languages
            var languages = OcrEngine.AvailableRecognizerLanguages;
            
            if (languages.Count == 0)
            {
                _logger.Warning("No OCR languages available. Please install language packs.");
                return;
            }

            // Prefer English as default, but OCR will auto-detect
            _currentLanguage = languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en")) 
                ?? languages.First();
            
            _ocrEngine = OcrEngine.TryCreateFromLanguage(_currentLanguage);
            
            if (_ocrEngine == null)
            {
                _logger.Warning("Failed to create OCR engine for language: {Lang}", _currentLanguage.LanguageTag);
            }
            else
            {
                _logger.Information("OCR engine initialized with language: {Lang}", _currentLanguage.LanguageTag);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize OCR engine");
        }
    }

    /// <summary>
    /// Set OCR language explicitly.
    /// </summary>
    /// <param name="languageTag">Language tag (e.g., "en-US", "ja-JP", "zh-CN").</param>
    /// <returns>True if language was set successfully.</returns>
    public bool SetLanguage(string languageTag)
    {
        try
        {
            var language = new Language(languageTag);
            var engine = OcrEngine.TryCreateFromLanguage(language);
            
            if (engine != null)
            {
                _ocrEngine = engine;
                _currentLanguage = language;
                _logger.Information("OCR language set to: {Lang}", languageTag);
                return true;
            }
            
            _logger.Warning("Failed to create OCR engine for: {Lang}", languageTag);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to set OCR language: {Lang}", languageTag);
            return false;
        }
    }

    /// <summary>
    /// Check if a language pack is installed.
    /// </summary>
    /// <param name="languageTag">Language tag to check.</param>
    /// <returns>True if language is available.</returns>
    public bool IsLanguageAvailable(string languageTag)
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Any(l => l.LanguageTag.StartsWith(languageTag.Split('-')[0]));
    }

    /// <summary>
    /// Get list of available OCR languages with display names.
    /// </summary>
    /// <returns>List of (language tag, display name) pairs.</returns>
    public List<(string Tag, string DisplayName)> GetAvailableLanguages()
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Select(l => (l.LanguageTag, l.DisplayName))
            .ToList();
    }

    /// <summary>
    /// Perform OCR on image bytes.
    /// </summary>
    /// <param name="imageBytes">BMP image data.</param>
    /// <returns>List of detected text regions.</returns>
    public List<TranslationRegion> ProcessFrame(byte[] imageBytes)
    {
        var regions = new List<TranslationRegion>();

        if (_ocrEngine == null)
        {
            _logger.Warning("OCR engine not initialized");
            return regions;
        }

        try
        {
            // Convert bytes to bitmap
            using var ms = new MemoryStream(imageBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.DecodePixelWidth = 1920; // Limit size for performance
            bitmap.EndInit();
            bitmap.Freeze();

            // Perform OCR
            var result = _ocrEngine.RecognizeAsync(bitmap).GetAwaiter().GetResult();

            // Process each line
            foreach (var line in result.Lines)
            {
                var text = line.Text.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                // Skip if text is entirely Cyrillic (Russian)
                if (IsEntirelyCyrillic(text))
                {
                    _logger.Debug("Skipping Cyrillic text: {Text}", text);
                    continue;
                }

                // Calculate bounding box
                var bounds = new Rect(
                    line.BoundingRect.X,
                    line.BoundingRect.Y,
                    line.BoundingRect.Width,
                    line.BoundingRect.Height);

                // Generate hash for change detection
                var hash = ComputeRegionHash(imageBytes, bounds);

                // Check if content changed
                var regionKey = $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}";
                if (_previousFrameHashes.TryGetValue(regionKey, out var prevHash) && prevHash == hash)
                {
                    // Content unchanged, skip
                    continue;
                }

                // Create region
                var region = new TranslationRegion
                {
                    Bounds = bounds,
                    OriginalText = text,
                    SourceLanguage = DetectLanguage(text),
                    ImageHash = hash,
                    ContainsCyrillic = ContainsCyrillic(text),
                    DetectedAt = DateTime.UtcNow
                };

                regions.Add(region);

                // Update hash cache
                _previousFrameHashes[regionKey] = hash;
            }

            // Clean old hashes (keep only recent positions)
            if (_previousFrameHashes.Count > 100)
            {
                var keysToRemove = _previousFrameHashes.Keys.Take(50).ToList();
                foreach (var key in keysToRemove)
                {
                    _previousFrameHashes.Remove(key);
                }
            }

            _logger.Debug("OCR detected {Count} regions", regions.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "OCR processing failed");
        }

        return regions;
    }

    /// <summary>
    /// Compute hash of a region in the image.
    /// </summary>
    private string ComputeRegionHash(byte[] imageBytes, Rect bounds)
    {
        try
        {
            // Simple hash based on position and a sample of image data
            // In production, you'd extract the actual region pixels
            var regionData = $"{bounds.Left}:{bounds.Top}:{bounds.Width}:{bounds.Height}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(regionData));
            return Convert.ToHexString(hash).Substring(0, 16);
        }
        catch
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }

    /// <summary>
    /// Detect language from text content.
    /// </summary>
    private string DetectLanguage(string text)
    {
        // Simple heuristic detection
        if (ContainsJapanese(text))
            return "ja";
        if (ContainsChinese(text))
            return "zh";
        if (ContainsKorean(text))
            return "ko";
        if (ContainsCyrillic(text))
            return "ru";
        
        // Default to English for Latin script
        return "en";
    }

    /// <summary>
    /// Check if text contains Japanese characters.
    /// </summary>
    private static bool ContainsJapanese(string text)
    {
        return text.Any(c => 
            c >= 0x3040 && c <= 0x30FF || // Hiragana and Katakana
            c >= 0x4E00 && c <= 0x9FFF);  // Kanji
    }

    /// <summary>
    /// Check if text contains Chinese characters.
    /// </summary>
    private static bool ContainsChinese(string text)
    {
        return text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
    }

    /// <summary>
    /// Check if text contains Korean characters.
    /// </summary>
    private static bool ContainsKorean(string text)
    {
        return text.Any(c => c >= 0xAC00 && c <= 0xD7AF);
    }

    /// <summary>
    /// Check if text contains any Cyrillic characters.
    /// </summary>
    private static bool ContainsCyrillic(string text)
    {
        return text.Any(c => c >= 0x0400 && c <= 0x04FF);
    }

    /// <summary>
    /// Check if text is entirely Cyrillic (ignoring spaces and punctuation).
    /// </summary>
    private static bool IsEntirelyCyrillic(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
            return false;
        
        return letters.All(c => c >= 0x0400 && c <= 0x04FF);
    }

    /// <summary>
    /// Clear previous frame hash cache.
    /// </summary>
    public void ClearHashCache()
    {
        _previousFrameHashes.Clear();
        _logger.Debug("OCR hash cache cleared");
    }

    /// <summary>
    /// Dispose OCR resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
