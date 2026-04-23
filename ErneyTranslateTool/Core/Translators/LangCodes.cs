using System;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Converts DeepL-style language codes (RU, EN-US, PT-BR, ZH...) into
/// the lowercase ISO-639-1 code that most non-DeepL APIs expect.
/// </summary>
internal static class LangCodes
{
    public static string ToIso2(string deeplCode)
    {
        if (string.IsNullOrWhiteSpace(deeplCode))
            return "en";
        var head = deeplCode.Split('-')[0];
        return head.ToLowerInvariant();
    }
}
