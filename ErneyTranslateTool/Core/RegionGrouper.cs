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
        => GroupWithIndices(input).Merged;

    /// <summary>
    /// Same algorithm as <see cref="Group"/> but also returns a per-input-index
    /// mapping <c>SourceToGroup[i]</c> = which merged-group <paramref name="input"/>[i]
    /// ended up in. Used by the engine's grouping-hysteresis layer to remember
    /// last frame's decision and re-apply it on a near-identical next frame
    /// (kills the merged-vs-split flicker that pure threshold tuning can't
    /// eliminate when OCR's per-frame bounding-box jitter pushes the gap
    /// across a threshold).
    /// </summary>
    public static (List<TranslationRegion> Merged, int[] SourceToGroup) GroupWithIndices(List<TranslationRegion> input)
    {
        if (input.Count == 0)
            return (new List<TranslationRegion>(), Array.Empty<int>());
        if (input.Count == 1)
            return (new List<TranslationRegion>(input), new[] { 0 });

        // Sort top-to-bottom, then left-to-right — but remember each region's
        // ORIGINAL input index so we can fill SourceToGroup in input order.
        var indexed = input
            .Select((r, i) => (Region: r, OriginalIndex: i))
            .OrderBy(t => t.Region.Bounds.Top)
            .ThenBy(t => t.Region.Bounds.Left)
            .ToList();

        var groupsOfRegions = new List<List<TranslationRegion>>();
        var groupOfOriginalIndex = new int[input.Count];

        foreach (var (region, originalIdx) in indexed)
        {
            int chosenGroup = -1;
            for (int gi = 0; gi < groupsOfRegions.Count; gi++)
            {
                if (CanJoin(groupsOfRegions[gi], region))
                {
                    groupsOfRegions[gi].Add(region);
                    chosenGroup = gi;
                    break;
                }
            }
            if (chosenGroup < 0)
            {
                chosenGroup = groupsOfRegions.Count;
                groupsOfRegions.Add(new List<TranslationRegion> { region });
            }
            groupOfOriginalIndex[originalIdx] = chosenGroup;
        }

        var merged = groupsOfRegions.Select(Merge).ToList();
        return (merged, groupOfOriginalIndex);
    }

    /// <summary>
    /// Apply a pre-decided grouping (from a previous frame's
    /// <see cref="GroupWithIndices"/> result) to a new set of input regions.
    /// <paramref name="sourceToGroup"/>[i] is the group number that
    /// <paramref name="input"/>[i] should land in. Regions sharing a group
    /// number are merged via the same logic as the regular grouper, so the
    /// resulting merged text and bounds are computed from the CURRENT frame's
    /// data — only the merge structure is inherited from the snapshot.
    /// </summary>
    public static List<TranslationRegion> ApplyGrouping(
        IList<TranslationRegion> input, IList<int> sourceToGroup)
    {
        if (input.Count == 0) return new List<TranslationRegion>();
        if (input.Count != sourceToGroup.Count)
            throw new ArgumentException("input.Count must equal sourceToGroup.Count");

        // Bucket inputs by group index. Preserve input order within each
        // bucket so Merge produces consistent text (the merge logic
        // concatenates in encounter order with smart hyphen / spacing
        // handling).
        var buckets = new SortedDictionary<int, List<TranslationRegion>>();
        for (int i = 0; i < input.Count; i++)
        {
            var g = sourceToGroup[i];
            if (!buckets.TryGetValue(g, out var list))
                buckets[g] = list = new List<TranslationRegion>();
            list.Add(input[i]);
        }

        // For Merge to produce correct text + bounds we want each bucket
        // sorted top-to-bottom (matches what Group does internally).
        return buckets.Values
            .Select(b => b.OrderBy(r => r.Bounds.Top).ThenBy(r => r.Bounds.Left).ToList())
            .Select(Merge)
            .ToList();
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

        // v1.0.22: bumped again. The v1.0.21 values (2.2 / 2.5) caught
        // most of the Yankee Massage VN's dialog wraps but still missed
        // some frames — OCR's per-frame bounding-box jitter would push
        // the gap above 2.2×H on roughly half the frames, producing
        // visible split-vs-merged flicker. Bumping to 2.8 / 3.0 gives a
        // comfortable margin above what OCR jitter realistically reports
        // for VN-style leading.
        //
        // Risk: longer non-continuation gaps could now false-merge,
        // particularly multi-word vertical menu items (e.g. "New Game" /
        // "Load Game" / "Settings"). Single-word ALL-CAPS button stacks
        // are still caught by the LooksLikeLabel guard above this switch.
        // If multi-word menu false-merges become common in real use,
        // a more robust fix is temporal grouping hysteresis (remember
        // the previous frame's merge decision and bias toward
        // consistency) — punted to a follow-up release.
        var maxGap = signal switch
        {
            LineEndSignal.StrongContinuation => lb.Height * 3.0,  // ", \n..." — definitely one sentence
            LineEndSignal.WeakContinuation   => lb.Height * 2.8,  // no punctuation — probably continues
            LineEndSignal.Terminator         => lb.Height * 0.55, // ". \n..." — almost certainly new sentence
            _                                => lb.Height * 0.7,  // unknown / fallback to old behaviour
        };
        if (gap > maxGap) return false;
        // Hard absolute upper bound: even strong continuation shouldn't
        // span more than ~3.5 line heights — at that point it's almost
        // certainly a layout boundary, not a wrap.
        if (gap > lb.Height * 3.5) return false;

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
