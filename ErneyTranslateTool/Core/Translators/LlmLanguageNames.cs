using System;
using System.Collections.Generic;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Maps the user-selected DeepL-style language code into the human-readable
/// English name we want to put into LLM system prompts ("Translate into
/// Russian" reads better to a model than "Translate into RU").
///
/// <para>Falls back to the raw code when nothing matches — the LLM will
/// usually still figure it out.</para>
/// </summary>
internal static class LlmLanguageNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RU"]   = "Russian",
        ["EN"]   = "English",
        ["EN-US"] = "English",
        ["EN-GB"] = "English",
        ["JA"]   = "Japanese",
        ["ZH"]   = "Chinese",
        ["KO"]   = "Korean",
        ["DE"]   = "German",
        ["FR"]   = "French",
        ["ES"]   = "Spanish",
        ["IT"]   = "Italian",
        ["PT"]   = "Portuguese",
        ["PT-BR"] = "Brazilian Portuguese",
        ["PL"]   = "Polish",
        ["NL"]   = "Dutch",
        ["UK"]   = "Ukrainian",
        ["CS"]   = "Czech",
        ["TR"]   = "Turkish",
        ["AR"]   = "Arabic",
        ["LV"]   = "Latvian",
        ["LT"]   = "Lithuanian",
        ["ET"]   = "Estonian",
        ["FI"]   = "Finnish",
        ["SV"]   = "Swedish",
        ["NB"]   = "Norwegian",
        ["DA"]   = "Danish",
        ["RO"]   = "Romanian",
        ["HU"]   = "Hungarian",
        ["EL"]   = "Greek",
    };

    public static string EnglishNameFor(string deeplCode)
    {
        if (string.IsNullOrWhiteSpace(deeplCode)) return "Russian";
        return Map.TryGetValue(deeplCode, out var name) ? name : deeplCode;
    }
}
