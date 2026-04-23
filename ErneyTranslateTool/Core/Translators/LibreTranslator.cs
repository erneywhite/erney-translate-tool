using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// LibreTranslate — open-source self-hostable translator. Public instances
/// vary in availability; some require an API key, some don't.
/// </summary>
public class LibreTranslator : ITranslator
{
    public string Name => "LibreTranslate";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly ILogger _logger;
    private bool _disposed;

    public LibreTranslator(string baseUrl, string? apiKey, ILogger logger)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://libretranslate.com" : baseUrl.TrimEnd('/');
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        var tgt = LangCodes.ToIso2(targetLanguage);
        var body = new
        {
            q = text,
            source = "auto",
            target = tgt,
            format = "text",
            api_key = _apiKey ?? string.Empty
        };

        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/translate", body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LibreTranslate HTTP {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("translatedText", out var t)
            ? (t.GetString() ?? text)
            : text;
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            var translated = await TranslateAsync("hello", "RU", ct);
            return (true, $"LibreTranslate ({_baseUrl}): соединение OK.\nПример: hello → {translated}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "LibreTranslate verify failed");
            return (false, $"LibreTranslate: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
