using System;
using ErneyTranslateTool.Data;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

public static class TranslatorFactory
{
    public const string ProviderDeepL = "DeepL";
    public const string ProviderMyMemory = "MyMemory";
    public const string ProviderGoogleFree = "GoogleFree";
    public const string ProviderLibreTranslate = "LibreTranslate";
    public const string ProviderOpenAI = "OpenAI";
    public const string ProviderAnthropic = "Anthropic";
    public const string ProviderGemini = "Gemini";
    public const string ProviderGroq = "Groq";

    public static readonly string[] AllProviders =
    {
        ProviderMyMemory,
        ProviderGoogleFree,
        ProviderDeepL,
        ProviderLibreTranslate,
        // The two free LLMs go before the paid ones — most users will land
        // on these and shouldn't have to scroll past the credit-card-only
        // options to find them.
        ProviderGemini,
        ProviderGroq,
        ProviderOpenAI,
        ProviderAnthropic,
    };

    public static string DisplayName(string provider) => provider switch
    {
        ProviderDeepL => "DeepL (нужен API-ключ + карта)",
        ProviderMyMemory => "MyMemory (бесплатно, email увеличивает лимит)",
        ProviderGoogleFree => "Google Translate (бесплатно, без регистрации)",
        ProviderLibreTranslate => "LibreTranslate (open source)",
        ProviderGemini => "Google Gemini (LLM, бесплатный free tier)",
        ProviderGroq => "Groq · Llama 3.3 (LLM, бесплатно, очень быстро)",
        ProviderOpenAI => "OpenAI (LLM, лучшее качество, нужен платный API-ключ)",
        ProviderAnthropic => "Anthropic Claude (LLM, лучшее качество, нужен платный API-ключ)",
        _ => provider
    };

    /// <summary>
    /// Build a translator instance from current settings. Returns null and a
    /// human-readable error if required credentials are missing.
    /// </summary>
    public static ITranslator? Create(AppSettings settings, ILogger logger, out string? error)
        => Create(settings.Config.TranslationProvider, settings, logger, out error);

    /// <summary>
    /// Build a translator for a specific provider id, regardless of the
    /// settings' primary one. Used by the fallback-provider machinery so
    /// the same factory can produce both ends of the dual-translator pair
    /// from one shared bag of credentials.
    /// </summary>
    public static ITranslator? Create(string? providerId, AppSettings settings, ILogger logger, out string? error)
    {
        error = null;
        var provider = providerId;
        if (string.IsNullOrWhiteSpace(provider))
            provider = ProviderMyMemory;

        switch (provider)
        {
            case ProviderDeepL:
            {
                var key = settings.GetApiKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "DeepL: API-ключ не настроен";
                    return null;
                }
                return new DeepLTranslator(key, logger);
            }
            case ProviderMyMemory:
                return new MyMemoryTranslator(settings.Config.MyMemoryEmail, logger);

            case ProviderGoogleFree:
                return new GoogleFreeTranslator(logger);

            case ProviderLibreTranslate:
                return new LibreTranslator(
                    settings.Config.LibreTranslateUrl,
                    settings.Config.LibreTranslateApiKey,
                    logger);

            case ProviderOpenAI:
            {
                var key = settings.GetOpenAIKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "OpenAI: API-ключ не настроен";
                    return null;
                }
                return new OpenAITranslator(
                    key,
                    settings.Config.OpenAIModel,
                    settings.Config.LlmTemperature,
                    settings.Config.LlmContextSize,
                    settings.Config.LlmUseContext,
                    logger);
            }

            case ProviderAnthropic:
            {
                var key = settings.GetAnthropicKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "Anthropic: API-ключ не настроен";
                    return null;
                }
                return new AnthropicTranslator(
                    key,
                    settings.Config.AnthropicModel,
                    settings.Config.LlmTemperature,
                    settings.Config.LlmContextSize,
                    settings.Config.LlmUseContext,
                    logger);
            }

            case ProviderGemini:
            {
                var key = settings.GetGeminiKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "Gemini: API-ключ не настроен";
                    return null;
                }
                return new GeminiTranslator(
                    key,
                    settings.Config.GeminiModel,
                    settings.Config.LlmTemperature,
                    settings.Config.LlmContextSize,
                    settings.Config.LlmUseContext,
                    logger);
            }

            case ProviderGroq:
            {
                var key = settings.GetGroqKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "Groq: API-ключ не настроен";
                    return null;
                }
                return new GroqTranslator(
                    key,
                    settings.Config.GroqModel,
                    settings.Config.LlmTemperature,
                    settings.Config.LlmContextSize,
                    settings.Config.LlmUseContext,
                    logger);
            }

            default:
                error = $"Неизвестный провайдер: {provider}";
                return null;
        }
    }
}
