using System.Collections.Generic;
using System.Linq;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core.Ocr;

public static class OcrTextHelpers
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

    /// <summary>
    /// v1.0.35: true when OCR "text" is really a picture — a wallpaper
    /// pattern read as "*", "$3", "C", "x($)". Real text has at least two
    /// letters in a row; CJK is exempt, one character there is a word.
    /// Tesseract has stricter checks of its own on top of this.
    /// </summary>
    public static bool LooksLikeNoise(string text)
    {
        int run = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) { run = 0; continue; }
            if (c >= 0x3040) return false; // kana, CJK, hangul
            if (++run >= 2) return false;
        }
        return true;
    }

    /// <summary>Remove <see cref="LooksLikeNoise"/> regions in place; returns how many were dropped.</summary>
    public static int DropNoise(List<TranslationRegion> regions) =>
        regions.RemoveAll(r => LooksLikeNoise(r.OriginalText ?? string.Empty));

    /// <summary>
    /// v1.0.35: lines this many times taller than the frame's typical line
    /// are logos and titles drawn as pictures ("妖狐的戀愛學程" on a title
    /// screen). Translating them covered the art with a plain box.
    /// </summary>
    public const double OversizedLineFactor = 1.8;

    /// <summary>
    /// Remove lines much taller than the median line of the frame, in place;
    /// returns how many were dropped. Needs a few lines to know what
    /// "typical" is — a screen with one or two lines is left alone.
    /// </summary>
    public static int DropOversized(List<TranslationRegion> regions)
    {
        if (regions.Count < 3) return 0;
        var heights = regions.Select(r => r.Bounds.Height).OrderBy(h => h).ToList();
        var median = heights[heights.Count / 2];
        return regions.RemoveAll(r => r.Bounds.Height > median * OversizedLineFactor);
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
