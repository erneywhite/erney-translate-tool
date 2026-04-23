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

        // Candidate sits below `last` with a gap no bigger than ~80% of one
        // line height — anything wider is a paragraph break, not a wrap.
        var gap = cb.Top - lb.Bottom;
        if (gap < -lb.Height * 0.3) return false; // overlapping rows: weird, skip
        if (gap > lb.Height * 0.8) return false;

        // Roughly the same column — left edges within a few characters of
        // each other, or candidate horizontally overlaps last.
        var sameLeftEdge = Math.Abs(cb.Left - lb.Left) <= lb.Height * 3;
        var horizontalOverlap = Math.Min(lb.Right, cb.Right) - Math.Max(lb.Left, cb.Left) > 0;
        if (!sameLeftEdge && !horizontalOverlap) return false;

        return true;
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
