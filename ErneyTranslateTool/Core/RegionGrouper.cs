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

        // v1.0.19 Bug A fix: WeakContinuation was too eager for vertical
        // menu stacks. A button list like "NORMAL\nHARD\nS.HARD" is three
        // single-word labels with no punctuation — pre-v1.0.19 my new
        // heuristic merged them into one phrase and translated it as
        // garbage ("РедVIeОкна"). Override the WeakContinuation extension
        // when the previous line looks like a label rather than a
        // paragraph wrap (single word, short, button-shaped).
        if (signal == LineEndSignal.WeakContinuation && LooksLikeLabel(last.OriginalText))
            signal = LineEndSignal.Unknown;

        // v1.0.20: bumped continuation thresholds. Visual novels and modern
        // dialogue boxes use generous leading (often 1.4–1.7×H) for
        // readability. The pre-v1.0.20 1.3×H weak-continuation cap missed
        // the long 3-line dialogue in Yankee Massage where line 1 ended on
        // "deem me" without punctuation — the gap to line 2 exceeded 1.3
        // and the heuristic split the sentence. Bumped to 1.7 to match
        // VN-style leading. The LooksLikeLabel guard above still keeps
        // genuine button stacks separated, so we don't over-merge menus.
        var maxGap = signal switch
        {
            LineEndSignal.StrongContinuation => lb.Height * 2.2,  // ", \n..." — definitely one sentence
            LineEndSignal.WeakContinuation   => lb.Height * 1.7,  // no punctuation — probably continues
            LineEndSignal.Terminator         => lb.Height * 0.55, // ". \n..." — almost certainly new sentence
            _                                => lb.Height * 0.7,  // unknown / fallback to old behaviour
        };
        if (gap > maxGap) return false;
        // Hard absolute upper bound: even strong continuation shouldn't
        // span more than ~2.8 line heights — at that point it's almost
        // certainly a layout boundary, not a wrap. Slightly bumped from
        // 2.5 to keep parity with the generous continuation thresholds
        // above; LooksLikeLabel + sameLeftEdge / horizontalOverlap guards
        // still prevent merges across genuinely separate panels.
        if (gap > lb.Height * 2.8) return false;

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

    /// <summary>
    /// Heuristic: does a line look like a UI label / menu button rather
    /// than a paragraph fragment? Used in v1.0.19 to keep the
    /// WeakContinuation gap extension from merging vertical button stacks
    /// like "NORMAL / HARD / S.HARD".
    ///
    /// <para>Signals (must hit ALL):</para>
    /// <list type="bullet">
    ///   <item>Single token (no internal whitespace) — paragraph wraps
    ///         have multiple words.</item>
    ///   <item>Short — &lt;= 20 chars trimmed. Real wrap fragments tend
    ///         to be longer; labels are concise.</item>
    ///   <item>No lowercase Latin/Cyrillic letters — buttons are
    ///         ALL CAPS or single capitalised word with no
    ///         "lowercase mid-sentence" character. This excludes
    ///         normal English/Russian sentence fragments while still
    ///         catching "Save", "QUIT", "Continue", "S.HARD", etc.</item>
    /// </list>
    ///
    /// <para>CJK strings (no spaces, no Latin/Cyrillic letters) pass two of
    /// the three checks but the lowercase one is irrelevant — to avoid
    /// breaking Japanese paragraph wraps, we require at least one Latin
    /// or Cyrillic uppercase letter as positive evidence of "button-shaped"
    /// text.</para>
    /// </summary>
    private static bool LooksLikeLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length > 20) return false;
        // Multiple words → paragraph fragment, not a label.
        foreach (var c in trimmed)
            if (char.IsWhiteSpace(c)) return false;

        // Walk the chars: any lowercase Latin/Cyrillic kills the label
        // hypothesis (sentences have lowercase letters in them); count
        // uppercase Latin/Cyrillic as positive evidence. Punctuation,
        // digits, CJK pass through neutrally.
        bool hasUpperLatinCyrillic = false;
        foreach (var c in trimmed)
        {
            if (IsLowerLatinCyrillic(c)) return false;
            if (IsUpperLatinCyrillic(c)) hasUpperLatinCyrillic = true;
        }
        return hasUpperLatinCyrillic;
    }

    private static bool IsLowerLatinCyrillic(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'а' && c <= 'я') || c == 'ё';

    private static bool IsUpperLatinCyrillic(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'А' && c <= 'Я') || c == 'Ё';

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
