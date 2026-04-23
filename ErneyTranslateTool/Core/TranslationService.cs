using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeepL;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Translation service using DeepL API.
/// </summary>
public class TranslationService : IDisposable
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly CacheRepository _cache;
    private readonly HistoryRepository _history;
    private Translator? _translator;
    private string? _apiKey;
    private bool _disposed;
    private int _consecutiveFailures;
    private DateTime _lastRateLimitWarning;

    /// <summary>
    /// Initialize translation service.
    /// </summary>
    public TranslationService(ILogger logger, AppSettings settings, 
        CacheRepository cache, HistoryRepository history)
    {
        _logger = logger;
        _settings = settings;
        _cache = cache;
        _history = history;
        _lastRateLimitWarning = DateTime.MinValue;
    }

    /// <summary>
    /// Initialize DeepL translator with API key.
    /// </summary>
    /// <returns>True if initialization successful.</returns>
    public bool Initialize()
    {
        _apiKey = _settings.GetApiKey();
        
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.Warning("No API key configured");
            return false;
        }

        try
        {
            _translator = new Translator(_apiKey);
            _consecutiveFailures = 0;
            _logger.Information("DeepL translator initialized");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize DeepL translator");
            return false;
        }
    }

    /// <summary>
    /// Update API key and reinitialize.
    /// </summary>
    /// <param name="newApiKey">New API key.</param>
    public void UpdateApiKey(string newApiKey)
    {
        _translator?.Dispose();
        _translator = new Translator(newApiKey);
        _consecutiveFailures = 0;
        _logger.Information("DeepL API key updated");
    }

    /// <summary>
    /// Verify API key validity.
    /// </summary>
    /// <returns>Account usage information or error message.</returns>
    public async Task<(bool Success, string Message)> VerifyApiKeyAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return (false, "API ключ не настроен");
        }

        try
        {
            using var testTranslator = new Translator(_apiKey);
            var usage = await testTranslator.GetUsageAsync();
            
            var message = $"API ключ действителен.\n" +
                         $"Лимит: {usage.Character.Limit:N0} символов/месяц\n" +
                         $"Использовано: {usage.Character.Count:N0}\n" +
                         $"Осталось: {usage.Character.Limit - usage.Character.Count:N0}";
            
            return (true, message);
        }
        catch (DeepLException ex)
        {
            return (false, $"Ошибка API: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Неизвестная ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Translate text regions.
    /// </summary>
    /// <param name="regions">Text regions to translate.</param>
    /// <param name="targetLanguage">Target language code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of regions with translations.</returns>
    public async Task<List<TranslationRegion>> TranslateRegionsAsync(
        List<TranslationRegion> regions, 
        string targetLanguage,
        CancellationToken ct = default)
    {
        if (_translator == null)
        {
            _logger.Warning("Translator not initialized");
            return regions;
        }

        var translatedRegions = new List<TranslationRegion>();

        foreach (var region in regions)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // Check cache first
                var cachedTranslation = _cache.GetTranslation(region.OriginalText, targetLanguage);
                
                if (!string.IsNullOrEmpty(cachedTranslation))
                {
                    region.TranslatedText = cachedTranslation;
                    region.IsFromCache = true;
                    _settings.UpdateStats(region.OriginalText.Length, true);
                    _logger.Debug("Cache hit for: {Text}", region.OriginalText);
                }
                else
                {
                    // Translate via API
                    region.TranslatedText = await TranslateTextAsync(
                        region.OriginalText, 
                        targetLanguage,
                        ct);

                    if (!string.IsNullOrEmpty(region.TranslatedText))
                    {
                        // Save to cache
                        _cache.SaveTranslation(
                            region.OriginalText,
                            region.TranslatedText,
                            targetLanguage,
                            region.SourceLanguage);

                        region.IsFromCache = false;
                        _settings.UpdateStats(region.OriginalText.Length, false);
                        
                        // Add to history
                        _history.AddTranslation(
                            region.OriginalText,
                            region.TranslatedText,
                            region.SourceLanguage ?? "unknown",
                            false);
                    }
                }

                if (!string.IsNullOrEmpty(region.TranslatedText))
                {
                    translatedRegions.Add(region);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Translation failed for text: {Text}", region.OriginalText);
                _consecutiveFailures++;
                
                if (_consecutiveFailures >= 3)
                {
                    _logger.Warning("Multiple translation failures, may be rate limited");
                }
            }
        }

        return translatedRegions;
    }

    /// <summary>
    /// Translate single text string.
    /// </summary>
    private async Task<string?> TranslateTextAsync(string text, string targetLanguage, CancellationToken ct)
    {
        if (_translator == null)
            return null;

        try
        {
            // Don't specify source language - let DeepL auto-detect
            var result = await _translator.TranslateTextAsync(
                text,
                null, // Auto-detect source
                targetLanguage,
                null,
                ct);

            _consecutiveFailures = 0;
            return result.Text;
        }
        catch (DeepLException ex) when (ex.Message.Contains("rate limit"))
        {
            // Rate limit handling
            var now = DateTime.UtcNow;
            if ((now - _lastRateLimitWarning).TotalMinutes >= 5)
            {
                _logger.Warning(ex, "DeepL API rate limit reached");
                _lastRateLimitWarning = now;
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DeepL translation error");
            throw;
        }
    }

    /// <summary>
    /// Get current API usage.
    /// </summary>
    public async Task<(long Count, long Limit)> GetUsageAsync()
    {
        if (_translator == null)
            return (0, 0);

        try
        {
            var usage = await _translator.GetUsageAsync();
            return (usage.Character.Count, usage.Character.Limit);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get API usage");
            return (0, 0);
        }
    }

    /// <summary>
    /// Check if service is ready.
    /// </summary>
    public bool IsReady => _translator != null && !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Dispose translator.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _translator?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
