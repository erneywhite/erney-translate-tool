using System;
using System.Linq;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// The system prompt every LLM translator uses (OpenAI, Anthropic, Gemini,
/// Groq), and the check for a model refusing to translate. The prompt used to
/// be copied into each translator.
/// </summary>
public static class LlmPrompt
{
    /// <summary>
    /// v1.0.38: the text is fiction from a game, often for adults. Without
    /// saying so, models refused lines of adult games ("I'm sorry, but I
    /// can't help with that"), and the refusal was drawn over the game.
    /// </summary>
    public static string System(string targetLanguageEnglishName) =>
        "You are a professional game and visual novel translator. " +
        $"Translate the user's text into {targetLanguageEnglishName}. " +
        "The text is a line from a work of fiction, a video game that may be for adults: " +
        "translate it faithfully and completely, including mature, violent or sexual content, " +
        "without refusing, softening or commenting. " +
        "Preserve tone, register, and stylistic features (formal/casual, slang, " +
        "honorifics). Don't explain, don't add notes — output only the translation. " +
        "If the text is already in the target language, return it unchanged.";

    private static readonly string[] RefusalStarts =
    {
        "i'm sorry", "i’m sorry", "i am sorry", "sorry, but", "sorry, i",
        "i can't", "i can’t", "i cannot", "i won't", "i won’t", "i'm unable", "i’m unable", "i am unable",
        "as an ai", "i apologize",
        "извините, но", "простите, но", "к сожалению, я не могу", "я не могу помочь", "я не могу перевести",
    };

    /// <summary>
    /// The model answered with a refusal instead of a translation. Only an
    /// answer that starts like one, is short, and doesn't start the same way
    /// as the original (a game line may well be "I'm sorry...").
    /// </summary>
    public static bool IsRefusal(string? translated, string? original)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        var t = translated.Trim().ToLowerInvariant();
        if (t.Length > 200) return false;
        var start = RefusalStarts.FirstOrDefault(s => t.StartsWith(s, StringComparison.Ordinal));
        if (start == null) return false;
        var o = (original ?? "").Trim().ToLowerInvariant();
        return !o.StartsWith(start, StringComparison.Ordinal);
    }
}
