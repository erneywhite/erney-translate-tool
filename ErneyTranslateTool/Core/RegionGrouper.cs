using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Stitches OCR-detected lines that look like they belong to the same paragraph
/// back into a single region. Tesseract returns text line-by-line so a
/// dialog box that wraps to three lines arrives as three independent strings —
/// translating each fragment in isolation produces awkward, incomplete output.
/// Grouping the lines back into one logical paragraph fixes that.
/// </summary>
public static class RegionGrouper
{
    /// <summary>
    /// Merge regions that are vertically adjacent, similarly aligned, and the
    /// same approximate font size. Anything that doesn't have a neighbour
    /// passes through unchanged.
    /// </summary>
    public static List<TranslationRegion> Group(List<TranslationRegion> input)
    {
        if (input.Count <= 1) return input;

        // Sort top-to-bottom, then left-to-right.
        var sorted = input
            .OrderBy(r => r.Bounds.Top)
            .ThenBy(r => r.Bounds.Left)
            .ToList();

        var groups = new List<List<TranslationRegion>>();
        foreach (var r in sorted)
        {
            var added = false;
            foreach (var g in groups)
            {
                if (CanJoin(g, r))
                {
                    g.Add(r);
                    added = true;
                    break;
                }
            }
            if (!added)
                groups.Add(new List<TranslationRegion> { r });
        }

        return groups.Select(Merge).ToList();
    }

    private static bool CanJoin(List<TranslationRegion> group, TranslationRegion candidate)
    {
        // Compare against the most recent member — sorted order means it's
        // the row immediately above the candidate.
        var last = group[^1];
        var lb = last.Bounds;
        var cb = candidate.Bounds;

        // Same approximate font height (within ±35%).
        var hRatio = Math.Min(lb.Height, cb.Height) / Math.Max(lb.Height, cb.Height);
        if (hRatio < 0.65) return false;

        // Directional width check. A narrow box on top of a much wider one
        // is the "speaker name above dialog" pattern — don't merge those.
        // The opposite (wide line followed by a short tail like one wrapped
        // word) is just the end of a paragraph and SHOULD merge, otherwise
        // the translator only sees the first line and the trailing word
        // gets translated in isolation ("...маленькой" + "девочка." instead
        // of "...маленькой девочкой").
        if (lb.Width < cb.Width * 0.4) return false;

        // Gap threshold is text-aware (v1.0.17). Visual novels and modern
        // dialogue boxes use generous leading (gap ~1.0–1.5×H) for
        // readability, while UI menus space items even further apart. The
        // pre-v1.0.17 fixed 0.7×H threshold worked for tight game HUDs
        // but mis-classified VN paragraphs as "separate" — translating
        // each line independently produced incoherent fragments
        // ("...три\nгоды одиночества" instead of "...три года одиночества").
        //
        // We now read the tail of the previous line's TEXT to decide:
        //   - ends with comma/colon/semicolon → strong continuation
        //   - ends with no terminator at all  → weak continuation
        //   - ends with .!?。！？…             → sentence terminator
        // and stretch the allowed gap accordingly. A hard cap at 2.5×H
        // still prevents merging across big visual breaks (e.g. menu
        // buttons stacked vertically).
        var gap = cb.Top - lb.Bottom;
        if (gap < -lb.Height * 0.3) return false; // overlapping rows: weird, skip

        var signal = ClassifyLineEnd(last.OriginalText);
        var maxGap = signal switch
        {
            LineEndSignal.StrongContinuation => lb.Height * 1.8,  // ", \n..." — definitely one sentence
            LineEndSignal.WeakContinuation   => lb.Height * 1.3,  // no punctuation — probably continues
            LineEndSignal.Terminator         => lb.Height * 0.55, // ". \n..." — almost certainly new sentence
            _                                => lb.Height * 0.7,  // unknown / fallback to old behaviour
        };
        if (gap > maxGap) return false;
        // Hard absolute upper bound: even strong continuation shouldn't
        // span more than ~2.5 line heights — at that point it's almost
        // certainly a layout boundary, not a wrap.
        if (gap > lb.Height * 2.5) return false;

        // Roughly the same column — either left edges close (left-aligned
        // paragraph) or there's any horizontal overlap (centered text where
        // each line has different width but they share screen real estate).
        var sameLeftEdge = Math.Abs(cb.Left - lb.Left) <= lb.Height * 4;
        var horizontalOverlap = Math.Min(lb.Right, cb.Right) - Math.Max(lb.Left, cb.Left) > 0;
        if (!sameLeftEdge && !horizontalOverlap) return false;

        return true;
    }

    /// <summary>
    /// Classify what the tail of a line "wants" — does it look like the
    /// previous sentence finished, or is it asking for the next line to
    /// continue it? Used to relax or tighten the geometric gap threshold
    /// in <see cref="CanJoin"/>. Covers both Latin and CJK punctuation
    /// (Japanese visual novels — our most common stylized-text source —
    /// use 。 as period and 、 as comma).
    /// </summary>
    private static LineEndSignal ClassifyLineEnd(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LineEndSignal.Unknown;

        // Walk backwards past trailing whitespace + closing quotes/parens.
        // Quote/paren after a terminator still counts as a terminator
        // ("...he said." → period through "), but quote/paren after a
        // continuation should still read as continuation.
        var i = text.Length - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsClosingPunct(text[i]))) i--;
        if (i < 0) return LineEndSignal.Unknown;

        var c = text[i];
        if (IsTerminator(c)) return LineEndSignal.Terminator;
        if (IsStrongContinuation(c)) return LineEndSignal.StrongContinuation;

        // Hyphenated split ("convers-\nation") — strong continuation; the
        // Merge step will join without inserting a space.
        if (c == '-') return LineEndSignal.StrongContinuation;

        // Letter or digit at end with no terminator — weak continuation.
        // A single short word with no punctuation could also be a one-word
        // menu item, so we still cap the geometric gap (1.3×H), which keeps
        // genuinely separate stacked items apart.
        if (char.IsLetterOrDigit(c)) return LineEndSignal.WeakContinuation;

        return LineEndSignal.Unknown;
    }

    private static bool IsTerminator(char c)
        => c is '.' or '!' or '?' or '。' or '！' or '？' or '…' or '‼' or '⁇' or '⁈' or '⁉';

    private static bool IsStrongContinuation(char c)
        => c is ',' or ';' or ':' or '、' or '，' or '；' or '：';

    private static bool IsClosingPunct(char c)
        => c is '"' or '\'' or ')' or ']' or '}' or '»' or '』' or '」' or '”' or '’';

    private enum LineEndSignal
    {
        Unknown,
        Terminator,           // .!?。 — almost certainly end of sentence
        StrongContinuation,   // ,;:、 or trailing hyphen — definitely continues
        WeakContinuation,     // letter/digit without terminator — probably continues
    }

    private static TranslationRegion Merge(List<TranslationRegion> group)
    {
        if (group.Count == 1) return group[0];

        var minX = group.Min(r => r.Bounds.Left);
        var minY = group.Min(r => r.Bounds.Top);
        var maxX = group.Max(r => r.Bounds.Right);
        var maxY = group.Max(r => r.Bounds.Bottom);

        // Lines often end mid-word with a hyphen; collapse "...convers-\nation"
        // into "...conversation" rather than "...convers- ation".
        var first = group[0];
        var sb = new System.Text.StringBuilder(first.OriginalText.TrimEnd());
        for (int i = 1; i < group.Count; i++)
        {
            var nextText = group[i].OriginalText.TrimStart();
            if (sb.Length > 0 && sb[^1] == '-')
            {
                sb.Length--; // drop the hyphen
                sb.Append(nextText);
            }
            else
            {
                sb.Append(' ');
                sb.Append(nextText);
            }
        }

        return new TranslationRegion
        {
            Bounds = new Rect(minX, minY, maxX - minX, maxY - minY),
            OriginalText = sb.ToString(),
            SourceLanguage = first.SourceLanguage,
            ContainsCyrillic = group.Any(r => r.ContainsCyrillic),
            DetectedAt = DateTime.UtcNow
        };
    }
}
