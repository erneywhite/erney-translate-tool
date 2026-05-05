using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// LLM translator backed by Google's Gemini API (the
/// <c>generativelanguage.googleapis.com</c> endpoint, NOT Vertex AI).
/// Free tier on aistudio.google.com is generous: ~15 RPM and ~1M tokens/day
/// for the Flash models, which is plenty for real-time translation
/// overlay even in long sessions. Anyone with a Google account can grab
/// a key in 30 seconds.
///
/// <para>Differences vs OpenAI/Anthropic that this class has to handle:
/// the API key goes in the URL query string (<c>?key=...</c>) instead of
/// an Authorization header; the request body uses
/// <c>system_instruction</c> at the top level + <c>contents</c> with role
/// <c>"model"</c> instead of <c>"assistant"</c>; the response wraps text
/// in <c>candidates[0].content.parts[*].text</c>; and the streaming
/// endpoint is a separate URL path with <c>?alt=sse</c> appended.</para>
/// </summary>
public class GeminiTranslator : IStreamingTranslator
{
    /// <summary>Base URL — model id and method get appended at request time.</summary>
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    public string Name => "Gemini";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly double _temperature;
    private readonly int _contextSize;
    private readonly bool _useContext;
    private readonly LinkedList<(string Original, string Translated)> _context = new();
    private readonly object _contextLock = new();
    private bool _disposed;

    public GeminiTranslator(string apiKey, string model, double temperature,
        int contextSize, bool useContext, ILogger logger)
    {
        _logger = logger;
        _apiKey = apiKey ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.0-flash" : model;
        // Gemini accepts 0.0–2.0 like OpenAI; pin to that range.
        _temperature = Math.Clamp(temperature, 0.0, 2.0);
        _contextSize = Math.Clamp(contextSize, 0, 10);
        _useContext = useContext;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // No Authorization header — Gemini wants ?key= in the URL.
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var request = BuildRequest(text, targetName);
        var url = $"{BaseUrl}{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.PostAsJsonAsync(url, request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Gemini API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
        var translated = ExtractText(parsed)?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(translated))
            RememberContext(text, translated);

        return translated;
    }

    /// <summary>
    /// Streaming counterpart. Gemini's SSE wraps full
    /// <c>GenerateContentResponse</c> JSON objects per <c>data:</c> line,
    /// with each carrying the next text fragment in
    /// <c>candidates[0].content.parts[*].text</c>. Each yield carries the
    /// FULL accumulated translation. Conversation context updated only at
    /// end-of-stream.
    /// </summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        string text, string targetLanguage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var request = BuildRequest(text, targetName);
        var url = $"{BaseUrl}{_model}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(_apiKey)}";

        using var content = JsonContent.Create(request,
            options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Gemini API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var accumulated = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:")) continue;

            var payload = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(payload)) continue;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                // Walk candidates[0].content.parts[*].text and concatenate
                // every text part in this chunk — usually there's just one
                // but the schema technically allows multiple.
                if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                    && candidates.GetArrayLength() > 0
                    && candidates[0].TryGetProperty("content", out var contentEl)
                    && contentEl.TryGetProperty("parts", out var parts))
                {
                    var sb = new StringBuilder();
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.Append(t.GetString());
                    }
                    if (sb.Length > 0) delta = sb.ToString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(delta)) continue;
            accumulated.Append(delta);
            var snapshot = accumulated.ToString();
            yield return accumulated.Length == delta.Length ? snapshot.TrimStart() : snapshot;
        }

        var final = accumulated.ToString().Trim();
        if (!string.IsNullOrEmpty(final))
            RememberContext(text, final);
    }

    /// <summary>
    /// Build the request body. System prompt goes into <c>system_instruction</c>
    /// (Gemini-specific top-level field), conversation history into
    /// <c>contents</c> with role <c>"model"</c> for past assistant turns
    /// (Gemini doesn't accept <c>"assistant"</c>).
    /// </summary>
    private GeminiRequest BuildRequest(string text, string targetLanguageEnglishName)
    {
        var contents = new List<GeminiContent>(capacity: _contextSize * 2 + 1);

        if (_useContext && _contextSize > 0)
        {
            lock (_contextLock)
            {
                foreach (var pair in _context)
                {
                    contents.Add(new GeminiContent
                    {
                        Role = "user",
                        Parts = new List<GeminiPart> { new() { Text = pair.Original } }
                    });
                    contents.Add(new GeminiContent
                    {
                        Role = "model",
                        Parts = new List<GeminiPart> { new() { Text = pair.Translated } }
                    });
                }
            }
        }

        contents.Add(new GeminiContent
        {
            Role = "user",
            Parts = new List<GeminiPart> { new() { Text = text } }
        });

        return new GeminiRequest
        {
            SystemInstruction = new GeminiSystem
            {
                Parts = new List<GeminiPart>
                {
                    new() { Text =
                        $"You are a professional game and visual novel translator. " +
                        $"Translate the user's text into {targetLanguageEnglishName}. " +
                        "Preserve tone, register, and stylistic features (formal/casual, slang, " +
                        "honorifics). Don't explain, don't add notes — output only the translation. " +
                        "If the text is already in the target language, return it unchanged."
                    }
                }
            },
            Contents = contents,
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = _temperature,
                MaxOutputTokens = 1000,
            }
        };
    }

    private static string? ExtractText(GeminiResponse? resp)
    {
        var parts = resp?.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts == null || parts.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var p in parts)
            if (!string.IsNullOrEmpty(p.Text)) sb.Append(p.Text);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private void RememberContext(string original, string translated)
    {
        if (!_useContext || _contextSize <= 0) return;
        lock (_contextLock)
        {
            _context.AddLast((original, translated));
            while (_context.Count > _contextSize) _context.RemoveFirst();
        }
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            var probe = new GeminiRequest
            {
                SystemInstruction = new GeminiSystem
                {
                    Parts = new List<GeminiPart> { new() { Text = "You are a translator. Translate to Russian. Output only the translation." } }
                },
                Contents = new List<GeminiContent>
                {
                    new()
                    {
                        Role = "user",
                        Parts = new List<GeminiPart> { new() { Text = "hello" } }
                    }
                },
                GenerationConfig = new GeminiGenerationConfig { Temperature = 0.0, MaxOutputTokens = 50 }
            };
            var url = $"{BaseUrl}{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
            using var resp = await _http.PostAsJsonAsync(url, probe,
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"Gemini: {(int)resp.StatusCode} — {Truncate(body, 200)}");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
            var sample = ExtractText(parsed)?.Trim() ?? "?";
            return (true, $"Gemini ({_model}): соединение OK.\nПример: hello → {sample}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Gemini verify failed");
            return (false, $"Gemini: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? string.Empty) : s.Substring(0, max) + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // === DTOs ===

    private class GeminiRequest
    {
        [JsonPropertyName("system_instruction")] public GeminiSystem? SystemInstruction { get; set; }
        [JsonPropertyName("contents")] public List<GeminiContent> Contents { get; set; } = new();
        [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private class GeminiSystem
    {
        [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = new();
    }

    private class GeminiContent
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = new();
    }

    private class GeminiPart
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    private class GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; }
    }

    private class GeminiResponse
    {
        [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
    }
}
