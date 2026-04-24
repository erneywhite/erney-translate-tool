using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Translation orchestrator. Wraps a pluggable <see cref="ITranslator"/> backend
/// (DeepL / MyMemory / GoogleFree / LibreTranslate) with caching and history.
/// </summary>
public class TranslationService : IDisposable
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly CacheRepository _cache;
    private readonly HistoryRepository _history;
    private ITranslator? _translator;
    private bool _disposed;
    private DateTime _lastRateLimitWarning = DateTime.MinValue;
    private int _consecutiveFailures;

    public TranslationService(ILogger logger, AppSettings settings,
        CacheRepository cache, HistoryRepository history)
    {
        _logger = logger;
        _settings = settings;
        _cache = cache;
        _history = history;
    }

    /// <summary>
    /// Build (or rebuild) the underlying translator from current settings.
    /// </summary>
    public bool Initialize()
    {
        try
        {
            _translator?.Dispose();
            _translator = TranslatorFactory.Create(_settings, _logger, out var error);
            if (_translator == null)
            {
                _logger.Warning("Translator init failed: {Error}", error);
                return false;
            }
            _consecutiveFailures = 0;
            _logger.Information("Translator initialized: {Name}", _translator.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize translator");
            return false;
        }
    }

    public bool IsReady => _translator != null;

    public string CurrentProvider => _translator?.Name ?? "(none)";

    /// <summary>
    /// Force rebuild of the translator (call after settings change).
    /// </summary>
    public void Reload() => Initialize();

    /// <summary>
    /// Verify current provider credentials with a small live call.
    /// </summary>
    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        if (_translator == null && !Initialize())
            return (false, "Провайдер перевода не настроен");
        return await _translator!.VerifyAsync(ct);
    }

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
            if (ct.IsCancellationRequested) break;

            try
            {
                var cached = _cache.GetTranslation(region.OriginalText, targetLanguage);
                if (!string.IsNullOrEmpty(cached))
                {
                    region.TranslatedText = cached;
                    region.IsFromCache = true;
                    _settings.UpdateStats(region.OriginalText.Length, true);
                }
                else
                {
                    region.TranslatedText = await _translator.TranslateAsync(
                        region.OriginalText, targetLanguage, ct);

                    if (!string.IsNullOrWhiteSpace(region.TranslatedText))
                    {
                        _cache.SaveTranslation(
                            region.OriginalText,
                            region.TranslatedText,
                            targetLanguage,
                            region.SourceLanguage);

                        // Cheap (no-op unless we're 10 % over the configured
                        // limit) and runs on the threadpool — won't block this
                        // hot path. 0 here means "no limit, never evict".
                        var maxBytes = _settings.Config.MaxCacheSizeMb > 0
                            ? (long)_settings.Config.MaxCacheSizeMb * 1024 * 1024
                            : 0L;
                        if (maxBytes > 0)
                            _cache.EnforceSizeLimitInBackground(maxBytes);

                        region.IsFromCache = false;
                        _settings.UpdateStats(region.OriginalText.Length, false);
                        _history.AddTranslation(
                            region.OriginalText,
                            region.TranslatedText,
                            region.SourceLanguage ?? "unknown",
                            false);
                    }
                    _consecutiveFailures = 0;
                }

                if (!string.IsNullOrWhiteSpace(region.TranslatedText))
                    translatedRegions.Add(region);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error(ex, "Translation failed for: {Text}", region.OriginalText);
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastRateLimitWarning).TotalMinutes >= 5)
                    {
                        _logger.Warning("Multiple translation failures — possible rate limit on {Provider}", CurrentProvider);
                        _lastRateLimitWarning = now;
                    }
                }
            }
        }

        return translatedRegions;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _translator?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
