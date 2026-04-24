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
/// LLM translator backed by Anthropic's Messages API. Same shape as
/// <see cref="OpenAITranslator"/>: paid API key, conversation context
/// for narrative continuity, system prompt that instructs Claude to
/// preserve tone and not add commentary.
///
/// <para>Anthropic's API differs from OpenAI's in three small but
/// important ways: the system prompt lives in its own top-level
/// <c>system</c> field (not inside <c>messages</c>), <c>max_tokens</c>
/// is mandatory not optional, and authentication uses <c>x-api-key</c>
/// + a required <c>anthropic-version</c> header rather than a Bearer
/// token.</para>
/// </summary>
public class AnthropicTranslator : IStreamingTranslator
{
    private const string EndpointUrl = "https://api.anthropic.com/v1/messages";
    /// <summary>Required by Anthropic — pinned to a known stable date so we don't break if they ship a breaking version bump.</summary>
    private const string ApiVersion = "2023-06-01";

    public string Name => "Anthropic";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _model;
    private readonly double _temperature;
    private readonly int _contextSize;
    private readonly bool _useContext;
    private readonly LinkedList<(string Original, string Translated)> _context = new();
    private readonly object _contextLock = new();
    private bool _disposed;

    public AnthropicTranslator(string apiKey, string model, double temperature,
        int contextSize, bool useContext, ILogger logger)
    {
        _logger = logger;
        _model = string.IsNullOrWhiteSpace(model) ? "claude-haiku-4-5" : model;
        // Anthropic clamps temperature to [0, 1] — values > 1 return 400.
        _temperature = Math.Clamp(temperature, 0.0, 1.0);
        _contextSize = Math.Clamp(contextSize, 0, 10);
        _useContext = useContext;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var (system, messages) = BuildPayload(text, targetName);

        var request = new MessagesRequest
        {
            Model = _model,
            System = system,
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
                $"Anthropic API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken: ct);
        // Anthropic returns content as an array of "blocks"; the text block's
        // .text is what we want. Single-block text responses are the norm.
        var translated = parsed?.Content?.FirstOrDefault(b => b.Type == "text")?.Text?.Trim()
                         ?? string.Empty;

        if (!string.IsNullOrEmpty(translated))
            RememberContext(text, translated);

        return translated;
    }

    private (string system, List<MessagePart> messages) BuildPayload(string text, string targetLanguageEnglishName)
    {
        var system =
            $"You are a professional game and visual novel translator. " +
            $"Translate the user's text into {targetLanguageEnglishName}. " +
            "Preserve tone, register, and stylistic features (formal/casual, slang, " +
            "honorifics). Don't explain, don't add notes — output only the translation. " +
            "If the text is already in the target language, return it unchanged.";

        var messages = new List<MessagePart>(capacity: _contextSize * 2 + 1);

        if (_useContext && _contextSize > 0)
        {
            lock (_contextLock)
            {
                foreach (var pair in _context)
                {
                    messages.Add(new MessagePart { Role = "user", Content = pair.Original });
                    messages.Add(new MessagePart { Role = "assistant", Content = pair.Translated });
                }
            }
        }

        messages.Add(new MessagePart { Role = "user", Content = text });
        return (system, messages);
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
    /// Streaming counterpart to <see cref="TranslateAsync"/>. Anthropic's
    /// SSE format is more verbose than OpenAI's — it sends typed events
    /// (<c>message_start</c>, <c>content_block_start</c>,
    /// <c>content_block_delta</c>, <c>content_block_stop</c>,
    /// <c>message_delta</c>, <c>message_stop</c>) and we only care about
    /// the text-delta payloads:
    /// <code>
    /// event: content_block_delta
    /// data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Доб"}}
    /// </code>
    /// Each yield carries the FULL accumulated translation. LLM context
    /// is updated only at end-of-stream.
    /// </summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        string text, string targetLanguage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var targetName = LlmLanguageNames.EnglishNameFor(targetLanguage);
        var (system, messages) = BuildPayload(text, targetName);

        var request = new MessagesRequest
        {
            Model = _model,
            System = system,
            Messages = messages,
            Temperature = _temperature,
            MaxTokens = 1000,
            Stream = true,
        };

        using var content = JsonContent.Create(request,
            options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = content };
        // ResponseHeadersRead so the body stream stays open for chunked reads
        // instead of being fully buffered before we get a chance to look.
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Anthropic API {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        var accumulated = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            // We can ignore the "event:" line entirely — the type is also
            // inside the JSON payload as the "type" field.
            if (!line.StartsWith("data:")) continue;

            var payload = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(payload)) continue;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type != "content_block_delta") continue;

                if (root.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("type", out var deltaTypeEl)
                    && deltaTypeEl.GetString() == "text_delta"
                    && deltaEl.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    delta = textEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(delta)) continue;
            accumulated.Append(delta);
            var snapshot = accumulated.ToString();
            // Same leading-space defence as OpenAI — Claude rarely does it
            // but no reason not to be safe.
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
            var probe = new MessagesRequest
            {
                Model = _model,
                System = "You are a translator. Translate to Russian. Output only the translation.",
                Messages = new List<MessagePart>
                {
                    new() { Role = "user", Content = "hello" },
                },
                Temperature = 0.0,
                MaxTokens = 50,
            };

            using var resp = await _http.PostAsJsonAsync(EndpointUrl, probe,
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"Anthropic: {(int)resp.StatusCode} — {Truncate(body, 200)}");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken: ct);
            var sample = parsed?.Content?.FirstOrDefault(b => b.Type == "text")?.Text?.Trim() ?? "?";
            return (true, $"Anthropic ({_model}): соединение OK.\nПример: hello → {sample}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Anthropic verify failed");
            return (false, $"Anthropic: {ex.Message}");
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

    private class MessagesRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("system")] public string System { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<MessagePart> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        // Default null → JsonIgnoreCondition.WhenWritingNull keeps it out
        // of the regular non-streaming request. Set to true by
        // TranslateStreamAsync.
        [JsonPropertyName("stream")] public bool? Stream { get; set; }
    }

    private class MessagePart
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private class MessagesResponse
    {
        [JsonPropertyName("content")] public List<ContentBlock>? Content { get; set; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
