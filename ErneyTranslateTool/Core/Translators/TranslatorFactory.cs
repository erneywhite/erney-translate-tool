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

    public static readonly string[] AllProviders =
    {
        ProviderMyMemory,
        ProviderGoogleFree,
        ProviderDeepL,
        ProviderLibreTranslate
    };

    public static string DisplayName(string provider) => provider switch
    {
        ProviderDeepL => "DeepL (нужен API-ключ + карта)",
        ProviderMyMemory => "MyMemory (бесплатно, email увеличивает лимит)",
        ProviderGoogleFree => "Google Translate (бесплатно, без регистрации)",
        ProviderLibreTranslate => "LibreTranslate (open source)",
        _ => provider
    };

    /// <summary>
    /// Build a translator instance from current settings. Returns null and a
    /// human-readable error if required credentials are missing.
    /// </summary>
    public static ITranslator? Create(AppSettings settings, ILogger logger, out string? error)
    {
        error = null;
        var provider = settings.Config.TranslationProvider;
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

            default:
                error = $"Неизвестный провайдер: {provider}";
                return null;
        }
    }
}
