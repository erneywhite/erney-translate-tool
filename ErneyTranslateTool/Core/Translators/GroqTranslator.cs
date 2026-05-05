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
/// LLM translator backed by Groq Cloud's OpenAI-compatible Chat Completions
/// API. Free tier with API key from console.groq.com — anyone with a Google
/// or GitHub account can grab one in 30 seconds. Quality on
/// <c>llama-3.3-70b-versatile</c> (default) is solid for translation,
/// notably for English/Russian/Japanese; speed is Groq's signature
/// advantage — 250-400 tokens/sec, several times faster than OpenAI/Anthropic.
///
/// <para>API surface mirrors OpenAI almost exactly (same /chat/completions
/// schema, same SSE delta format), so this class is structurally a near-copy
/// of <see cref="OpenAITranslator"/> with a different endpoint and default
/// model. Kept as separate classes (rather than a shared base) for
/// debuggability — one provider failing shouldn't make us suspect a
/// shared abstraction.</para>
/// </summary>
public class GroqTranslator : IStreamingTranslator
{
    private const string EndpointUrl = "https://api.groq.com/openai/v1/chat/completions";

    public string Name => "Groq";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _model;
    private readonly double _temperature;
    private readonly int _contextSize;
    private readonly bool _useContext;
    private readonly LinkedList<(string Original, string Translated)> _context = new();
    private readonly object _contextLock = new();
    private bool _disposed;

    public GroqTranslator(string apiKey, string model, double temperature,
        int contextSize, bool useContext, ILogger logger)
    {
        _logger = logger;
        _model = string.IsNullOrWhiteSpace(model) ? "llama-3.3-70b-versatile" : model;
        _temperature = Math.Clamp(temperature, 0.0, 2.0);
        _contextSize = Math.Clamp(contextSize, 0, 10);
        _useContext = useContext;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var messages = BuildMessages(text, targetName);

        var request = new ChatRequest
        {
            Model = _model,
            Messages = messages,
            Temperature = _temperature,
            MaxTokens = 1000,
        };

        using var resp = await _http.PostAsJsonAsync(EndpointUrl, request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Groq API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        var translated = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(translated))
            RememberContext(text, translated);

        return translated;
    }

    /// <summary>
    /// Streaming counterpart. Groq's SSE format matches OpenAI's exactly:
    /// <c>data: {"choices":[{"delta":{"content":"..."}}]}</c> until
    /// <c>data: [DONE]</c>. Each yield carries the full accumulated
    /// translation; conversation context is updated only at end-of-stream.
    /// </summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        string text, string targetLanguage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var messages = BuildMessages(text, targetName);

        var request = new ChatRequest
        {
            Model = _model,
            Messages = messages,
            Temperature = _temperature,
            MaxTokens = 1000,
            Stream = true,
        };

        using var content = JsonContent.Create(request,
            options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Groq API {(int)resp.StatusCode}: {Truncate(body, 300)}");
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
            if (payload == "[DONE]") break;
            if (string.IsNullOrEmpty(payload)) continue;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    delta = contentEl.GetString();
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

    private List<ChatMessage> BuildMessages(string text, string targetLanguageEnglishName)
    {
        var messages = new List<ChatMessage>(capacity: _contextSize * 2 + 2)
        {
            new()
            {
                Role = "system",
                Content =
                    $"You are a professional game and visual novel translator. " +
                    $"Translate the user's text into {targetLanguageEnglishName}. " +
                    "Preserve tone, register, and stylistic features (formal/casual, slang, " +
                    "honorifics). Don't explain, don't add notes — output only the translation. " +
                    "If the text is already in the target language, return it unchanged.",
            },
        };

        if (_useContext && _contextSize > 0)
        {
            lock (_contextLock)
            {
                foreach (var pair in _context)
                {
                    messages.Add(new ChatMessage { Role = "user", Content = pair.Original });
                    messages.Add(new ChatMessage { Role = "assistant", Content = pair.Translated });
                }
            }
        }

        messages.Add(new ChatMessage { Role = "user", Content = text });
        return messages;
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
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a translator. Translate to Russian. Output only the translation." },
                new() { Role = "user", Content = "hello" },
            };
            using var resp = await _http.PostAsJsonAsync(EndpointUrl,
                new ChatRequest { Model = _model, Messages = messages, Temperature = 0.0, MaxTokens = 50 },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"Groq: {(int)resp.StatusCode} — {Truncate(body, 200)}");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
            var sample = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? "?";
            return (true, $"Groq ({_model}): соединение OK.\nПример: hello → {sample}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Groq verify failed");
            return (false, $"Groq: {ex.Message}");
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

    // === DTOs (mirror OpenAI's exactly — Groq is API-compatible) ===

    private class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("stream")] public bool? Stream { get; set; }
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private class ChatResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; set; }
    }

    private class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
