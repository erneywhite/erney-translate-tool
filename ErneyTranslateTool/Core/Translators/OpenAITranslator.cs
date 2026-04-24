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
/// LLM translator backed by OpenAI's Chat Completions API. Significantly
/// better quality than the rules-based providers (DeepL/MyMemory/Google)
/// for game dialogue, visual novels and other narrative text — at the
/// cost of needing the user's own paid API key.
///
/// <para>
/// Carries a sliding window of the last few exchanges as conversation
/// context so the model can resolve pronouns ("he", "they") and continue
/// jokes/callbacks within a scene. Context size is user-configurable and
/// bounded so token spend stays predictable.
/// </para>
/// </summary>
public class OpenAITranslator : IStreamingTranslator
{
    private const string EndpointUrl = "https://api.openai.com/v1/chat/completions";

    public string Name => "OpenAI";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _model;
    private readonly double _temperature;
    private readonly int _contextSize;
    private readonly bool _useContext;
    // FIFO queue of recent (original, translated) pairs. Capped at
    // _contextSize entries so token cost is bounded.
    private readonly LinkedList<(string Original, string Translated)> _context = new();
    private readonly object _contextLock = new();
    private bool _disposed;

    public OpenAITranslator(string apiKey, string model, double temperature,
        int contextSize, bool useContext, ILogger logger)
    {
        _logger = logger;
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
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
            // Generous cap: a typical translated dialogue line is well
            // under 200 tokens. Big buffer means we never truncate mid-
            // sentence on long subtitles.
            MaxTokens = 1000,
        };

        using var resp = await _http.PostAsJsonAsync(EndpointUrl, request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Surface OpenAI's own error JSON when available — much more
            // useful than a bare status code in the log.
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"OpenAI API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        var translated = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(translated))
            RememberContext(text, translated);

        return translated;
    }

    /// <summary>
    /// Build the messages array for one request. System prompt sets the
    /// translator's role; optional sliding-window history of past
    /// (user, assistant) pairs gives the model conversation context.
    /// </summary>
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

    /// <summary>
    /// Streaming counterpart to <see cref="TranslateAsync"/>. Issues the
    /// same Chat Completions request with <c>stream: true</c> and parses
    /// OpenAI's SSE format:
    /// <code>
    /// data: {"choices":[{"delta":{"content":"Доб"}}]}
    /// data: {"choices":[{"delta":{"content":"ро"}}]}
    /// data: {"choices":[{"delta":{},"finish_reason":"stop"}]}
    /// data: [DONE]
    /// </code>
    /// Each yield carries the FULL accumulated translation so the caller
    /// can hand it straight to the overlay without separately accumulating.
    /// LLM context (sliding window) is updated only once at end-of-stream
    /// so we don't pollute it with partial fragments.
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
        // ResponseHeadersRead so we don't buffer the whole body — without it
        // HttpClient waits for the response to finish before returning,
        // defeating the entire point of streaming.
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"OpenAI API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var accumulated = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            // SSE comment lines start with ":" — used as keep-alives, ignore.
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
                // Stray non-JSON line (rare) — skip without poisoning the stream.
                continue;
            }

            if (string.IsNullOrEmpty(delta)) continue;
            accumulated.Append(delta);
            // Trim only the leading whitespace progressively — the model
            // sometimes prefixes the first token with a space; users would
            // see " Перевод..." which looks broken.
            var snapshot = accumulated.ToString();
            yield return accumulated.Length == delta.Length ? snapshot.TrimStart() : snapshot;
        }

        var final = accumulated.ToString().Trim();
        if (!string.IsNullOrEmpty(final))
            RememberContext(text, final);
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            // Tiny ping — translates "hello" so we both check the key and
            // confirm the model can answer. Bypasses sliding context to
            // keep the request as small as possible.
            var saved = _useContext;
            try
            {
                // Disable context for the probe by reflection of the field
                // would be nasty; we just call the API directly with no
                // history rather than going through TranslateAsync.
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
                    return (false, $"OpenAI: {(int)resp.StatusCode} — {Truncate(body, 200)}");
                }
                var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
                var sample = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? "?";
                return (true, $"OpenAI ({_model}): соединение OK.\nПример: hello → {sample}");
            }
            finally
            {
                _ = saved; // marker — keep the structure obvious for future edits
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "OpenAI verify failed");
            return (false, $"OpenAI: {ex.Message}");
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

    // === DTOs (internal — only this class touches them) ===

    private class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        // Default false → JsonIgnoreCondition.WhenWritingNull keeps it out
        // of the payload for the regular non-streaming path. Set to true
        // explicitly by TranslateStreamAsync.
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
