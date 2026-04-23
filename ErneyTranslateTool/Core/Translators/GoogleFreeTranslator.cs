using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Public Google Translate "gtx" endpoint — no API key, no signup, no card.
/// Unofficial; Google may rate-limit or change it without notice. Best for
/// low-volume personal use.
/// </summary>
public class GoogleFreeTranslator : ITranslator
{
    public string Name => "GoogleFree";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private bool _disposed;

    public GoogleFreeTranslator(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Pretend to be a browser — Google sometimes blocks bare clients.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        var tgt = LangCodes.ToIso2(targetLanguage);
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={tgt}&dt=t&q={Uri.EscapeDataString(text)}";

        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);

        // Response shape: [[["translated","original",...],...], ...]
        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
        {
            if (seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }
        return sb.ToString();
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            var translated = await TranslateAsync("hello", "RU", ct);
            return (true, $"Google (бесплатный): соединение OK.\nПример: hello → {translated}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GoogleFree verify failed");
            return (false, $"Google (бесплатный): {ex.Message}");
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
