using System;
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
        var logo = regions.Where(r => r.Bounds.Height > median * OversizedLineFactor).ToList();
        // A logo's other line can come out smaller ("NIGHT" 51 px, "ADVENTURE"
        // 43 px at a 26 px median): a big line right next to a dropped one
        // goes too. A big sign standing alone (a "START MSG" clipboard) stays.
        bool grew;
        do
        {
            grew = false;
            foreach (var r in regions)
            {
                if (logo.Contains(r) || r.Bounds.Height < median * LogoNeighbourFactor) continue;
                if (logo.Any(l => Touches(l.Bounds, r.Bounds, Math.Min(l.Bounds.Height, r.Bounds.Height) * 0.5)))
                {
                    logo.Add(r);
                    grew = true;
                }
            }
        } while (grew);
        return regions.RemoveAll(logo.Contains);
    }

    /// <summary>How big a line next to a logo must be to count as part of it.</summary>
    public const double LogoNeighbourFactor = 1.4;

    private static bool Touches(System.Windows.Rect a, System.Windows.Rect b, double gap)
    {
        a.Inflate(gap, gap);
        return a.IntersectsWith(b);
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
