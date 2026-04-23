using System.Linq;

namespace ErneyTranslateTool.Core.Ocr;

internal static class OcrTextHelpers
{
    public static bool ContainsJapanese(string text) => text.Any(c =>
        (c >= 0x3040 && c <= 0x30FF) ||  // Hiragana, Katakana
        (c >= 0x4E00 && c <= 0x9FFF));   // CJK Unified Ideographs (Kanji)

    public static bool ContainsChinese(string text) =>
        text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    public static bool ContainsKorean(string text) =>
        text.Any(c => c >= 0xAC00 && c <= 0xD7AF);

    public static bool ContainsCyrillic(string text) =>
        text.Any(c => c >= 0x0400 && c <= 0x04FF);

    public static bool IsEntirelyCyrillic(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(c => c >= 0x0400 && c <= 0x04FF);
    }

    public static string DetectLanguage(string text)
    {
        if (ContainsJapanese(text)) return "ja";
        if (ContainsChinese(text)) return "zh";
        if (ContainsKorean(text)) return "ko";
        if (ContainsCyrillic(text)) return "ru";
        return "en";
    }
}
