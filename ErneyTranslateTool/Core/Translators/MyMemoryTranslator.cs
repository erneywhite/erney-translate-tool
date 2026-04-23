using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// MyMemory free translation API. 5 000 chars/day anonymous,
/// 50 000 chars/day with a valid email — no card required.
/// Docs: https://mymemory.translated.net/doc/spec.php
/// </summary>
public class MyMemoryTranslator : ITranslator
{
    public string Name => "MyMemory";

    private readonly HttpClient _http;
    private readonly string? _email;
    private readonly ILogger _logger;
    private bool _disposed;

    public MyMemoryTranslator(string? email, ILogger logger)
    {
        _email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        var tgt = LangCodes.ToIso2(targetLanguage);
        var emailParam = _email != null ? $"&de={Uri.EscapeDataString(_email)}" : "";
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=Autodetect|{tgt}{emailParam}";

        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);

        if (doc.RootElement.TryGetProperty("responseStatus", out var status))
        {
            var code = status.ValueKind == JsonValueKind.Number ? status.GetInt32() : 200;
            if (code != 200 && doc.RootElement.TryGetProperty("responseDetails", out var detail))
                throw new InvalidOperationException("MyMemory: " + detail.GetString());
        }

        return doc.RootElement
            .GetProperty("responseData")
            .GetProperty("translatedText")
            .GetString() ?? text;
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            var translated = await TranslateAsync("hello", "RU", ct);
            var who = _email == null ? "анонимно (5 000 симв./день)" : $"email: {_email} (50 000 симв./день)";
            return (true, $"MyMemory: соединение OK — {who}.\nПример: hello → {translated}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MyMemory verify failed");
            return (false, $"MyMemory: {ex.Message}");
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
