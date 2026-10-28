using System;
using System.Collections.Generic;
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

    /// <summary>
    /// MyMemory refuses queries over 500 characters ("QUERY LENGTH LIMIT
    /// EXCEEDED"). A long paragraph — a web page, a codex entry — goes out in
    /// pieces that end on a sentence, a bit under the limit.
    /// </summary>
    internal const int MaxQueryChars = 450;

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        var parts = SplitForLimit(text, MaxQueryChars);
        if (parts.Count == 1) return await TranslateOneAsync(text, targetLanguage, ct);
        var translated = new List<string>(parts.Count);
        foreach (var part in parts)
            translated.Add(await TranslateOneAsync(part, targetLanguage, ct));
        return string.Join(" ", translated);
    }

    private async Task<string> TranslateOneAsync(string text, string targetLanguage, CancellationToken ct)
    {
        var tgt = LangCodes.ToIso2(targetLanguage);
        var emailParam = _email != null ? $"&de={Uri.EscapeDataString(_email)}" : "";
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=Autodetect|{tgt}{emailParam}";

        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);

        // The status comes as a number or as a string ("403"); an error used
        // to slip through as "translated text" and was drawn over the game.
        if (doc.RootElement.TryGetProperty("responseStatus", out var status))
        {
            var code = status.ValueKind switch
            {
                JsonValueKind.Number => status.GetInt32(),
                JsonValueKind.String when int.TryParse(status.GetString(), out var c) => c,
                _ => 200,
            };
            if (code != 200)
            {
                var detail = doc.RootElement.TryGetProperty("responseDetails", out var d) ? d.GetString() : null;
                throw new InvalidOperationException($"MyMemory: {detail ?? "error " + code}");
            }
        }

        return doc.RootElement
            .GetProperty("responseData")
            .GetProperty("translatedText")
            .GetString() ?? text;
    }

    /// <summary>
    /// Split <paramref name="text"/> into pieces of at most <paramref name="max"/>
    /// characters, cutting after a sentence end where possible, else after a
    /// space, else hard.
    /// </summary>
    internal static List<string> SplitForLimit(string text, int max)
    {
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var cut = -1;
            for (int i = max - 1; i > max / 3; i--)
            {
                if (rest[i] is '.' or '!' or '?' or '。' or '！' or '？' or '…' && (i + 1 >= rest.Length || char.IsWhiteSpace(rest[i + 1])))
                {
                    cut = i + 1;
                    break;
                }
            }
            if (cut < 0) cut = rest.LastIndexOf(' ', max - 1, max - max / 3) is var sp and > 0 ? sp : max;
            parts.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }
        if (rest.Length > 0) parts.Add(rest);
        return parts;
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
