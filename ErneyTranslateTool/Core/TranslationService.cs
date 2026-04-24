using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Core.Glossary;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Translation orchestrator. Wraps two pluggable <see cref="ITranslator"/>
/// backends (a primary + an optional fallback) with caching, glossary and
/// history.
///
/// <para>
/// The fallback machinery is pure availability protection: if the primary
/// throws three times in a row (typical when the user blew their daily
/// quota or the upstream is temporarily down), we silently start serving
/// requests from the fallback. Every minute we sneak a try at the
/// primary again — once it answers, we switch back so the user gets
/// their preferred provider's quality whenever possible.
/// </para>
/// </summary>
public class TranslationService : IDisposable
{
    /// <summary>How many primary failures in a row trigger the switch to fallback.</summary>
    private const int FailureThreshold = 3;

    /// <summary>Backoff between primary recovery probes while we're on the fallback.</summary>
    private static readonly TimeSpan PrimaryRecoveryProbeInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly CacheRepository _cache;
    private readonly HistoryRepository _history;
    private readonly GlossaryApplier _glossary;
    private ITranslator? _primary;
    private ITranslator? _fallback;
    private bool _disposed;
    private DateTime _lastRateLimitWarning = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _usingFallback;
    private DateTime _lastPrimaryProbeAt = DateTime.MinValue;

    /// <summary>True when we've fallen back to the secondary translator.</summary>
    public bool IsUsingFallback => _usingFallback;

    /// <summary>Raised whenever the active translator switches (primary -> fallback or back). Payload is a human-readable status line for the UI.</summary>
    public event EventHandler<string>? FallbackStateChanged;

    public TranslationService(ILogger logger, AppSettings settings,
        CacheRepository cache, HistoryRepository history, GlossaryApplier glossary)
    {
        _logger = logger;
        _settings = settings;
        _cache = cache;
        _history = history;
        _glossary = glossary;
    }

    /// <summary>
    /// Build (or rebuild) the primary translator and, if configured and
    /// distinct, the fallback. Reset the failure counter and the
    /// "using fallback" flag — settings just changed, last session's
    /// state isn't meaningful any more.
    /// </summary>
    public bool Initialize()
    {
        try
        {
            _primary?.Dispose();
            _fallback?.Dispose();
            _primary = null;
            _fallback = null;
            _consecutiveFailures = 0;
            _usingFallback = false;
            _lastPrimaryProbeAt = DateTime.MinValue;

            _primary = TranslatorFactory.Create(_settings, _logger, out var error);
            if (_primary == null)
            {
                _logger.Warning("Primary translator init failed: {Error}", error);
                return false;
            }

            // Fallback only if explicitly chosen AND not the same id as
            // primary (a "MyMemory -> MyMemory" fallback would be a no-op).
            var fallbackId = _settings.Config.FallbackProvider;
            if (!string.IsNullOrWhiteSpace(fallbackId)
                && !string.Equals(fallbackId, _settings.Config.TranslationProvider, StringComparison.OrdinalIgnoreCase))
            {
                _fallback = TranslatorFactory.Create(fallbackId, _settings, _logger, out var fbError);
                if (_fallback == null)
                {
                    _logger.Information("Fallback translator '{Id}' init skipped: {Error}", fallbackId, fbError);
                }
                else
                {
                    _logger.Information("Translator initialised: primary={P}, fallback={F}",
                        _primary.Name, _fallback.Name);
                    return true;
                }
            }

            _logger.Information("Translator initialised: {Name} (no fallback)", _primary.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialise translators");
            return false;
        }
    }

    public bool IsReady => _primary != null;

    public string CurrentProvider => (_usingFallback ? _fallback : _primary)?.Name ?? "(none)";

    /// <summary>Force rebuild of both translators (call after settings change).</summary>
    public void Reload() => Initialize();

    /// <summary>Verify the primary's credentials with a small live call.</summary>
    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        if (_primary == null && !Initialize())
            return (false, "Провайдер перевода не настроен");
        return await _primary!.VerifyAsync(ct);
    }

    public async Task<List<TranslationRegion>> TranslateRegionsAsync(
        List<TranslationRegion> regions,
        string targetLanguage,
        Action<TranslationRegion>? onRegionUpdated = null,
        CancellationToken ct = default)
    {
        if (_primary == null)
        {
            _logger.Warning("Translator not initialised");
            return regions;
        }

        var translatedRegions = new List<TranslationRegion>();
        // Streaming is only worth the SSE plumbing for actual LLM providers;
        // cache/glossary hits return instantly so chunked delivery would be
        // pointless. The active-translator check happens per-region inside
        // the loop because the fallback machinery may swap providers
        // mid-frame.
        var streamingEnabled = _settings.Config.UseStreamingLlm && onRegionUpdated != null;

        foreach (var region in regions)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Step 1 (highest priority): exact glossary match — bypasses
                // both cache and translator entirely. See v1.0.8 notes.
                if (_glossary.TryGetExactMatch(region.OriginalText, targetLanguage, out var glossaryHit))
                {
                    region.TranslatedText = glossaryHit;
                    region.IsFromCache = false;
                    _history.AddTranslation(
                        region.OriginalText,
                        region.TranslatedText,
                        "glossary",
                        false);
                    if (!string.IsNullOrWhiteSpace(region.TranslatedText))
                    {
                        translatedRegions.Add(region);
                        onRegionUpdated?.Invoke(region);
                    }
                    continue;
                }

                var cached = _cache.GetTranslation(region.OriginalText, targetLanguage);
                if (!string.IsNullOrEmpty(cached))
                {
                    region.TranslatedText = cached;
                    region.IsFromCache = true;
                    _settings.UpdateStats(region.OriginalText.Length, true);
                }
                else
                {
                    // Pick streaming path only when it's actually useful:
                    // - user toggle is on
                    // - the active translator implements IStreamingTranslator
                    // - the engine gave us a per-region callback
                    // Otherwise fall through to the regular round-trip call.
                    var active = ResolveActiveForStreamingHint();
                    if (streamingEnabled && active is IStreamingTranslator streamer)
                    {
                        region.TranslatedText = await TranslateStreamWithFallbackAsync(
                            streamer, region, targetLanguage, onRegionUpdated!, ct);
                    }
                    else
                    {
                        region.TranslatedText = await TranslateWithFallbackAsync(
                            region.OriginalText, targetLanguage, ct);
                    }

                    if (!string.IsNullOrWhiteSpace(region.TranslatedText))
                    {
                        _cache.SaveTranslation(
                            region.OriginalText,
                            region.TranslatedText,
                            targetLanguage,
                            region.SourceLanguage);

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
                }

                if (!string.IsNullOrWhiteSpace(region.TranslatedText))
                {
                    // Step 4 (post-process): glossary word-boundary replace.
                    region.TranslatedText = _glossary.Apply(region.TranslatedText, targetLanguage);
                    translatedRegions.Add(region);
                    // Final notify so the overlay shows the post-glossary
                    // text. For streaming this overrides the last partial
                    // chunk; for non-streaming this is the only notify.
                    onRegionUpdated?.Invoke(region);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error(ex, "Translation failed for: {Text}", region.OriginalText);
                if (_consecutiveFailures >= FailureThreshold)
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

    /// <summary>
    /// Pick which translator the streaming path should target right now —
    /// fallback if we've switched, primary otherwise. Doesn't touch the
    /// fallback state machine (probing/recovery stays inside
    /// <see cref="TranslateWithFallbackAsync"/>); this is just a hint
    /// used to decide whether to enter the streaming codepath at all.
    /// </summary>
    private ITranslator? ResolveActiveForStreamingHint()
        => _usingFallback && _fallback != null ? _fallback : _primary;

    /// <summary>
    /// Streaming version of <see cref="TranslateWithFallbackAsync"/>: yields
    /// partial text into the region (and forwards each chunk to the
    /// callback) as the LLM's SSE stream produces it. On hard failure of
    /// the primary it falls back to the regular non-streaming translator
    /// path so the user still gets a result, just without the typewriter
    /// animation.
    /// </summary>
    private async Task<string> TranslateStreamWithFallbackAsync(
        IStreamingTranslator streamer,
        TranslationRegion region,
        string targetLanguage,
        Action<TranslationRegion> onRegionUpdated,
        CancellationToken ct)
    {
        try
        {
            string final = string.Empty;
            await foreach (var chunk in streamer.TranslateStreamAsync(region.OriginalText, targetLanguage, ct))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                region.TranslatedText = chunk;
                final = chunk;
                onRegionUpdated(region);
            }
            // Reset failure counter on success of an active-primary stream
            // so the existing fallback machinery's invariants hold.
            if (!_usingFallback) _consecutiveFailures = 0;
            return final;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Streaming translate failed; degrading to non-streaming path");
            // Hand off to the non-streaming fallback machinery — same as if
            // streaming had been off in the first place. The user loses the
            // typewriter effect for this region but still gets a result.
            return await TranslateWithFallbackAsync(region.OriginalText, targetLanguage, ct);
        }
    }

    /// <summary>
    /// Run a single translation request through the dual-translator state
    /// machine: try the primary, switch to the fallback after enough
    /// consecutive failures, and periodically probe the primary so a
    /// recovered upstream gets us back to it.
    /// </summary>
    private async Task<string> TranslateWithFallbackAsync(
        string text, string targetLanguage, CancellationToken ct)
    {
        var primary = _primary!;
        var fallback = _fallback;

        // While we're on the fallback, periodically probe primary to see
        // whether the upstream came back. We do this on the actual user's
        // request (not a separate background task) so a recovered primary
        // is observed naturally without burning idle cycles.
        if (_usingFallback && fallback != null
            && DateTime.UtcNow - _lastPrimaryProbeAt > PrimaryRecoveryProbeInterval)
        {
            _lastPrimaryProbeAt = DateTime.UtcNow;
            try
            {
                var probe = await primary.TranslateAsync(text, targetLanguage, ct);
                if (!string.IsNullOrWhiteSpace(probe))
                {
                    _usingFallback = false;
                    _consecutiveFailures = 0;
                    _logger.Information("Primary {P} recovered, switching back from fallback", primary.Name);
                    FallbackStateChanged?.Invoke(this,
                        LanguageManager.Format("Strings.Engine.FallbackOff", primary.Name));
                    return probe;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Primary recovery probe failed; staying on fallback");
                // Don't increment _consecutiveFailures — that counter is for
                // the active path, not for opportunistic probes.
            }
        }

        var active = _usingFallback && fallback != null ? fallback : primary;

        try
        {
            var result = await active.TranslateAsync(text, targetLanguage, ct);
            if (active == primary) _consecutiveFailures = 0;
            return result;
        }
        catch (Exception ex)
        {
            if (active == primary)
            {
                _consecutiveFailures++;
                _logger.Information(ex, "Primary {P} failed ({N}/{Max})",
                    primary.Name, _consecutiveFailures, FailureThreshold);

                if (_consecutiveFailures >= FailureThreshold && fallback != null && !_usingFallback)
                {
                    _usingFallback = true;
                    _lastPrimaryProbeAt = DateTime.UtcNow; // throttle next probe
                    _logger.Warning("Primary {P} hit failure threshold — switching to fallback {F}",
                        primary.Name, fallback.Name);
                    FallbackStateChanged?.Invoke(this,
                        LanguageManager.Format("Strings.Engine.FallbackOn", primary.Name, fallback.Name));

                    // Retry this request on the fallback so the user doesn't
                    // see the trigger request as a loss.
                    try
                    {
                        return await fallback.TranslateAsync(text, targetLanguage, ct);
                    }
                    catch (Exception fbEx)
                    {
                        _logger.Error(fbEx, "Fallback {F} also failed", fallback.Name);
                        throw;
                    }
                }
            }
            else
            {
                _logger.Error(ex, "Fallback {F} failed", fallback?.Name);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _primary?.Dispose();
        _fallback?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
