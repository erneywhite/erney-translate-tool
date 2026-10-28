using System;
using System.Collections.Generic;
using System.Linq;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// The frame-to-frame layout logic of the pipeline, moved out of
/// TranslationEngine in v1.0.35 so it can be replayed without a game, a
/// window or a translator: grouping with hysteresis (v1.0.23/24), OCR-jitter
/// stabilisation (v1.0.19) and full-frame reuse (v1.0.25/30). The code is
/// the engine's, moved verbatim; only the window-rect lookup stayed behind.
/// Tests feed recorded OCR frames through it to measure layout flicker.
/// </summary>
public sealed class FrameLayout
{
    private readonly ILogger _logger;

    public FrameLayout(ILogger logger)
    {
        _logger = logger;
    }

    // v1.0.19: small ring of recently-translated (sourceText, bounds) pairs
    // used to suppress OCR jitter. When the same dialog is on screen across
    // frames, OCR sometimes returns slightly different text each frame
    // (",." swap, stray period, "I"/"l" confusion, …) — that breaks cache
    // hits and produces visually identical translations that flicker
    // between two slightly-different versions. Before each frame's
    // translation pass we snap each region's source text to its near-
    // identical neighbour from a recent frame, so cache lookups hit and
    // the overlay stays stable.
    private const int RecentRegionCapacity = 24;
    private readonly LinkedList<RecentRegion> _recentRegions = new();
    private readonly object _recentRegionsLock = new();
    private readonly record struct RecentRegion(string SourceText, System.Windows.Rect Bounds);

    // v1.0.23: grouping-hysteresis ring. The pure-threshold approach in
    // RegionGrouper (1.0.17–1.0.22) reached its ceiling — when OCR
    // bounding-box jitter pushes the line gap across the WeakContinuation
    // threshold from frame to frame, the same scene flips between
    // "merged into one paragraph" and "split into two" producing visible
    // overlay flicker. We now also remember the LAST FEW frames'
    // grouping decisions and replay them when the new frame's raw region
    // layout matches a recent snapshot — locking in whichever decision
    // we made first instead of letting jitter relitigate it every frame.
    private const int RecentGroupingsCapacity = 3;
    private readonly LinkedList<GroupingSnapshot> _recentGroupings = new();
    private readonly object _recentGroupingsLock = new();
    /// <summary>One frame's worth of grouping memory. RawBounds[i] is the input region position; SourceToGroup[i] is which merged group it landed in.</summary>
    private readonly record struct GroupingSnapshot(System.Windows.Rect[] RawBounds, int[] SourceToGroup);

    // v1.0.25: full-frame translation reuse. The previous v1.0.23/v1.0.24
    // hysteresis layers tried to make the GROUPING decision consistent
    // across frames, but in real testing OCR-jitter still produced
    // alternating output because the underlying group text fluctuated
    // (and so the cache + hysteresis layers below couldn't fully
    // prevent re-querying the translator with subtly different text).
    //
    // The user-suggested fix sidesteps the whole problem: if the new
    // frame's RAW OCR output matches the previous frame's raw output
    // closely enough (every new region maps to a previous region by
    // position AND text within Levenshtein tolerance), reuse the
    // previous frame's TRANSLATED regions verbatim — no re-grouping,
    // no re-translation, just re-render the same bubbles. The overlay
    // stops flickering because we literally stop computing different
    // outputs when the source hasn't meaningfully changed.
    private FrameSnapshot? _lastCompletedFrame;
    private readonly object _lastCompletedFrameLock = new();
    private sealed record FrameSnapshot(
        System.Windows.Rect[] RawBounds,
        string[] RawTexts,
        List<TranslationRegion> Translated,
        int[] Missed); // per Translated block: frames in a row its source text was missing

    /// <summary>Forget everything — a new session starts on a fresh window.</summary>
    public void Reset()
    {
        lock (_recentRegionsLock) _recentRegions.Clear();
        lock (_recentGroupingsLock) _recentGroupings.Clear();
        lock (_lastCompletedFrameLock) _lastCompletedFrame = null;
        lock (_heldLock) _held.Clear();
    }

    // v1.0.35: blocks shown last time, with how many frames in a row each
    // has been missing from OCR.
    private readonly object _heldLock = new();
    private List<(TranslationRegion Region, int Missed)> _held = new();
    // Blocks the last HoldBriefDropouts added, with their miss count, so the
    // reuse snapshot keeps counting instead of starting them again from 0.
    private readonly Dictionary<TranslationRegion, int> _lastHeld = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// v1.0.35: per-block version of the engine's "text gone for N frames"
    /// rule. OCR drops a small label ("SE") for a frame now and then; the
    /// frame got regrouped without it and the block blinked out and back.
    /// A block that was on screen stays for up to <paramref name="confirmFrames"/>-1
    /// missing frames, unless something new now covers its place.
    /// Returns <paramref name="translated"/> plus the blocks kept.
    /// </summary>
    public List<TranslationRegion> HoldBriefDropouts(List<TranslationRegion> translated, int confirmFrames)
    {
        lock (_heldLock)
        {
            var result = new List<TranslationRegion>(translated);
            var next = translated.Select(r => (r, 0)).ToList();
            var missing = _held
                .Where(h => !translated.Any(t => RectOverlapRatio(t.Bounds, h.Region.Bounds) > 0))
                .ToList();
            // Most of the screen changed: a new scene, not an OCR hiccup.
            // Holding the old blocks only kept stale text up for seconds.
            var sceneChange = missing.Count * 2 > _held.Count;
            _lastHeld.Clear();
            foreach (var (old, missed) in sceneChange ? new() : missing)
            {
                if (missed + 1 >= confirmFrames) continue;
                result.Add(old);
                next.Add((old, missed + 1));
                _lastHeld[old] = missed + 1;
            }
            _held = next;
            return result;
        }
    }

    /// <summary>
    /// v1.0.25 short-circuit. If the previous fully-processed frame's raw
    /// OCR output matches the new frame's within jitter tolerance, render
    /// the previous translated regions verbatim over the current window
    /// rect (which may have moved if the user dragged the window) and
    /// signal the caller to bail before grouping/translation.
    ///
    /// <para>"Matches" requires every new raw region to find a snapshot
    /// raw region with both ≥0.6 spatial overlap AND text within a
    /// Levenshtein tolerance. We're strict about new content: if even
    /// one new region has no spatial+text match in the snapshot, we
    /// assume genuine new text appeared and let the full pipeline run.
    /// Snapshot regions that DON'T appear in the new frame are
    /// tolerated — OCR can briefly miss small elements without
    /// invalidating the scene.</para>
    ///
    /// <para>v1.0.35: a block whose source text is missing stays in
    /// <paramref name="shown"/> until it has been missing for
    /// <paramref name="confirmFrames"/> frames in a row, then it is dropped
    /// from the snapshot for good. (v1.0.30 counted this per frame in the
    /// engine and never reset it once a block had left: from then on any
    /// one-frame OCR miss hid a block at once, and small labels blinked.)</para>
    /// </summary>
    public bool TryReusePreviousFrame(
        List<TranslationRegion> newRawRegions,
        int confirmFrames,
        out List<TranslationRegion>? shown)
    {
        shown = null;

        FrameSnapshot? snap;
        lock (_lastCompletedFrameLock) snap = _lastCompletedFrame;
        if (snap == null) return false;
        if (newRawRegions.Count == 0) return false;

        // Each new region must match a snapshot region by both position
        // and text. snapshotUsed prevents two new regions claiming the
        // same snapshot region (which would be a false-positive match).
        var snapshotUsed = new bool[snap.RawBounds.Length];
        foreach (var r in newRawRegions)
        {
            int bestJ = -1;
            double bestOverlap = 0.6;
            for (int j = 0; j < snap.RawBounds.Length; j++)
            {
                if (snapshotUsed[j]) continue;
                var ov = RectOverlapRatio(r.Bounds, snap.RawBounds[j]);
                if (ov > bestOverlap)
                {
                    bestOverlap = ov;
                    bestJ = j;
                }
            }
            if (bestJ < 0) return false; // new region with no positional match → genuinely new content
            if (!RawTextsMatch(r.OriginalText, snap.RawTexts[bestJ])) return false;
            snapshotUsed[bestJ] = true;
        }

        shown = new List<TranslationRegion>(snap.Translated.Count);
        var gone = false;
        for (int k = 0; k < snap.Translated.Count; k++)
        {
            var t = snap.Translated[k];
            var visible = false;
            for (int j = 0; j < snap.RawBounds.Length && !visible; j++)
                visible = snapshotUsed[j] && RectOverlapRatio(t.Bounds, snap.RawBounds[j]) > 0;
            snap.Missed[k] = visible ? 0 : snap.Missed[k] + 1;
            if (snap.Missed[k] < confirmFrames) shown.Add(t);
            else gone = true;
        }
        if (gone)
        {
            var keep = Enumerable.Range(0, snap.Translated.Count).Where(k => snap.Missed[k] < confirmFrames).ToList();
            lock (_lastCompletedFrameLock)
            {
                if (ReferenceEquals(_lastCompletedFrame, snap))
                    _lastCompletedFrame = snap with
                    {
                        Translated = keep.Select(k => snap.Translated[k]).ToList(),
                        Missed = keep.Select(k => snap.Missed[k]).ToArray(),
                    };
            }
        }
        _logger.Debug("Frame: reused previous translation ({N} regions, {V} shown)",
            snap.Translated.Count, shown.Count);
        return true;
    }

    /// <summary>Levenshtein-tolerant text match (same threshold curve as the per-region jitter stabiliser).</summary>
    private static bool RawTextsMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        var lenA = a?.Length ?? 0;
        var lenB = b?.Length ?? 0;
        if (Math.Abs(lenA - lenB) > Math.Max(3, Math.Max(lenA, lenB) / 6)) return false;
        var threshold = Math.Min(3, Math.Max(1, Math.Max(lenA, lenB) / 7));
        return BoundedLevenshtein(a ?? string.Empty, b ?? string.Empty, threshold) <= threshold;
    }

    /// <summary>Snapshot the just-completed frame's raw OCR + translated output for the next frame's reuse-shortcut.</summary>
    public void RememberCompletedFrame(List<TranslationRegion> rawRegions, List<TranslationRegion> translated)
    {
        var bounds = new System.Windows.Rect[rawRegions.Count];
        var texts = new string[rawRegions.Count];
        for (int i = 0; i < rawRegions.Count; i++)
        {
            bounds[i] = rawRegions[i].Bounds;
            texts[i] = rawRegions[i].OriginalText ?? string.Empty;
        }
        // Defensive copy so a later mutation of `translated` (we don't do
        // any but the API's caller could) doesn't poison the snapshot.
        var translatedCopy = new List<TranslationRegion>(translated);

        lock (_lastCompletedFrameLock)
        {
            int[] missed;
            lock (_heldLock)
                missed = translatedCopy.Select(t => _lastHeld.TryGetValue(t, out var m) ? m : 0).ToArray();
            _lastCompletedFrame = new FrameSnapshot(bounds, texts, translatedCopy, missed);
        }
    }

    /// <summary>
    /// v1.0.24 hysteresis entry point. The previous (v1.0.23) implementation
    /// required an exact region-count match between the new frame and a
    /// recent snapshot; in real Yankee-Massage frames that mostly failed
    /// because PaddleOCR sometimes picks up a noise region in the
    /// background pattern (or misses a small element) — the count
    /// fluctuates and the snapshot was rejected, so we fell back to the
    /// jitter-prone heuristic and the user kept seeing the merged-vs-split
    /// flicker.
    ///
    /// <para>The new approach has two pieces:</para>
    /// <list type="number">
    ///   <item>Per-region inheritance — every current region independently
    ///         searches the snapshots for an overlapping bound; matched
    ///         regions inherit that snapshot's group id, unmatched ones
    ///         get an isolated id. Tolerates count drift, noise regions,
    ///         and disappearing elements.</item>
    ///   <item>Merge-bias arbitration — we always also run the regular
    ///         heuristic and pick whichever produces FEWER groups (more
    ///         merging). This means once any frame manages to merge a
    ///         paragraph correctly, the snapshot inherits that decision
    ///         and locks it in; conversely, an unlucky early "split"
    ///         decision is overridden the moment a later frame's
    ///         heuristic produces a merge. False splits are sticky no
    ///         longer — false merges still are, but the LooksLikeLabel
    ///         and hRatio guards make those rare in practice.</item>
    /// </list>
    /// </summary>
    public List<TranslationRegion> ApplyGroupingHysteresisOrGroup(List<TranslationRegion> rawRegions)
    {
        if (rawRegions.Count == 0) return rawRegions;

        // Always run the heuristic — provides a baseline AND lets merge-
        // bias arbitration run even when a snapshot exists.
        var (heuristicMerged, heuristicGroups) = RegionGrouper.GroupWithIndices(rawRegions);

        GroupingSnapshot[] snapshots;
        lock (_recentGroupingsLock) snapshots = _recentGroupings.ToArray();

        int[] chosenGroups = heuristicGroups;
        var chosenMerged = heuristicMerged;
        string chosenBy = "heuristic";

        if (snapshots.Length > 0)
        {
            var inheritedGroups = InheritGroupsPerRegion(rawRegions, snapshots);
            if (inheritedGroups != null)
            {
                var inheritedMerged = RegionGrouper.ApplyGrouping(rawRegions, inheritedGroups);
                // Merge-bias: fewer groups (more merging) wins. Equal
                // counts → keep heuristic to avoid stale snapshot lock-in
                // when the scene has actually changed but happens to
                // produce the same group count.
                if (inheritedMerged.Count < heuristicMerged.Count)
                {
                    chosenGroups = inheritedGroups;
                    chosenMerged = inheritedMerged;
                    chosenBy = "hysteresis";
                }
            }
        }

        // Always remember the WINNING decision so the snapshot ring stays
        // biased toward the most-merged outcome we've observed recently.
        RememberGroupingSnapshot(rawRegions, chosenGroups);

        if (heuristicMerged.Count != chosenMerged.Count)
        {
            _logger.Debug("Frame: grouping {Source} kept {N} groups (heuristic alone would have {M})",
                chosenBy, chosenMerged.Count, heuristicMerged.Count);
        }
        return chosenMerged;
    }

    /// <summary>
    /// For each region in the current frame, find a matching region in any
    /// recent snapshot (newest first) by spatial overlap. Matched regions
    /// inherit their snapshot's group id; unmatched regions get a fresh
    /// isolated id. Snapshot ids are offset per snapshot to avoid
    /// collisions across snapshots. Returns null only when NO region
    /// matched any snapshot — in that case the snapshots are stale (scene
    /// change) and we let the heuristic decide on its own.
    /// </summary>
    private static int[]? InheritGroupsPerRegion(
        List<TranslationRegion> current, GroupingSnapshot[] snapshots)
    {
        // Reserve a generous id range per snapshot so SnapshotGroupOffset *
        // snapshot index + groupId stays unique across all snapshots.
        // 10 000 is more than the realistic group count by orders of
        // magnitude.
        const int IdSpacePerSnapshot = 10_000;

        var result = new int[current.Count];
        bool anyInherited = false;
        // Unmatched regions get ids past everything snapshots could produce.
        int unmatchedNextId = (snapshots.Length + 1) * IdSpacePerSnapshot;

        for (int i = 0; i < current.Count; i++)
        {
            int matchSnapIdx = -1;
            int matchSnapGroup = -1;
            double matchOverlap = 0.6; // minimum overlap to count as a match

            // Walk newest → oldest. Stop at first match — newest is most
            // representative of the current scene.
            for (int snapIdx = snapshots.Length - 1; snapIdx >= 0; snapIdx--)
            {
                var snapshot = snapshots[snapIdx];
                for (int j = 0; j < snapshot.RawBounds.Length; j++)
                {
                    var ov = RectOverlapRatio(current[i].Bounds, snapshot.RawBounds[j]);
                    if (ov > matchOverlap)
                    {
                        matchOverlap = ov;
                        matchSnapIdx = snapIdx;
                        matchSnapGroup = snapshot.SourceToGroup[j];
                    }
                }
                if (matchSnapIdx == snapIdx) break; // got a match in this (newest reachable) snapshot
            }

            if (matchSnapIdx >= 0)
            {
                result[i] = matchSnapIdx * IdSpacePerSnapshot + matchSnapGroup;
                anyInherited = true;
            }
            else
            {
                result[i] = unmatchedNextId++;
            }
        }
        return anyInherited ? result : null;
    }

    private void RememberGroupingSnapshot(List<TranslationRegion> rawRegions, IList<int> sourceToGroup)
    {
        var rawBounds = new System.Windows.Rect[rawRegions.Count];
        for (int i = 0; i < rawRegions.Count; i++) rawBounds[i] = rawRegions[i].Bounds;
        var indices = sourceToGroup as int[] ?? sourceToGroup.ToArray();

        lock (_recentGroupingsLock)
        {
            _recentGroupings.AddLast(new GroupingSnapshot(rawBounds, indices));
            while (_recentGroupings.Count > RecentGroupingsCapacity)
                _recentGroupings.RemoveFirst();
        }
    }

    /// <summary>Intersection-area / smaller-area ratio. Picks the smaller box as denominator so a tiny region inside a big one still scores 1.0.</summary>
    private static double RectOverlapRatio(System.Windows.Rect a, System.Windows.Rect b)
    {
        var ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (ix <= 0 || iy <= 0) return 0;
        var inter = ix * iy;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 ? inter / smaller : 0;
    }

    /// <summary>
    /// For each region, look in the recent-regions ring for a near-identical
    /// source text at an overlapping position. If found, rewrite the
    /// region's source to that recent text — the translation cache will
    /// then hit the existing translation and the overlay stays stable
    /// frame-to-frame instead of flickering between two slightly-different
    /// OCR readings of the same line.
    /// </summary>
    public void StabiliseAgainstRecent(List<TranslationRegion> regions)
    {
        if (_recentRegions.Count == 0) return;
        // Snapshot under lock to avoid racing with RememberRecentRegions —
        // even though OnFrameCaptured is single-flighted, defensive copy is
        // cheap (24 entries) and avoids subtle invariants.
        RecentRegion[] snapshot;
        lock (_recentRegionsLock)
        {
            snapshot = _recentRegions.ToArray();
        }

        foreach (var r in regions)
        {
            var match = FindStableMatch(r, snapshot);
            if (match.HasValue && !ReferenceEquals(match.Value.SourceText, r.OriginalText)
                && match.Value.SourceText != r.OriginalText)
            {
                _logger.Debug("Frame: stabilised '{New}' -> '{Stable}' (jitter)",
                    Truncate(r.OriginalText, 40), Truncate(match.Value.SourceText, 40));
                r.OriginalText = match.Value.SourceText;
            }
        }
    }

    /// <summary>
    /// Find a recent region with a near-identical source text and an
    /// overlapping bounding box. Position overlap matters: the same word
    /// appearing in two different parts of the screen is two different
    /// things, even if the text matches.
    /// </summary>
    private static RecentRegion? FindStableMatch(TranslationRegion candidate, RecentRegion[] recent)
    {
        var cb = candidate.Bounds;
        // Newer entries are at the END of the ring (AddLast), and a recent
        // version is more likely to match — walk back-to-front so we check
        // newest first and short-circuit on the first hit.
        for (int i = recent.Length - 1; i >= 0; i--)
        {
            var r = recent[i];
            // Position check: at least 50 % bounding-box overlap. Cheap and
            // strict enough to disambiguate "Save" buttons in different
            // panels of the same frame.
            if (!RectsOverlapEnough(r.Bounds, cb, 0.5)) continue;

            // Text check: short-circuit on exact match (the common case
            // outside of jitter), then fall through to Levenshtein for
            // near-identical fragments. Threshold is the lesser of
            // 3 absolute and 15 % of length — a 20-char line tolerates 3
            // edits, a 100-char line tolerates 10.
            if (string.Equals(r.SourceText, candidate.OriginalText, StringComparison.Ordinal))
                return r;

            // Length-skew prefilter — Levenshtein scales with min(m,n) so
            // bail on grossly different lengths without running the DP.
            var lenA = r.SourceText.Length;
            var lenB = candidate.OriginalText.Length;
            if (Math.Abs(lenA - lenB) > Math.Max(3, Math.Max(lenA, lenB) / 6)) continue;

            var threshold = Math.Min(3, Math.Max(1, Math.Max(lenA, lenB) / 7));
            if (BoundedLevenshtein(r.SourceText, candidate.OriginalText, threshold) <= threshold)
                return r;
        }
        return null;
    }

    /// <summary>Append this frame's regions to the recent ring; cap at <see cref="RecentRegionCapacity"/>.</summary>
    public void RememberRecentRegions(List<TranslationRegion> regions)
    {
        lock (_recentRegionsLock)
        {
            foreach (var r in regions)
            {
                if (string.IsNullOrWhiteSpace(r.OriginalText)) continue;
                _recentRegions.AddLast(new RecentRegion(r.OriginalText, r.Bounds));
                while (_recentRegions.Count > RecentRegionCapacity)
                    _recentRegions.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Bounded Levenshtein distance — bails out as soon as the running
    /// minimum of any row exceeds <paramref name="threshold"/>. Lets us
    /// reject far-apart strings cheaply without filling the full DP table.
    /// O(m*n) worst case; in the early-exit common case it's much less.
    /// </summary>
    private static int BoundedLevenshtein(string a, string b, int threshold)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Two-row DP: previous + current. Saves memory vs full m*n table.
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            // Early exit — if every cell of this row exceeds the threshold,
            // the final answer (bottom-right cell) can only get larger.
            if (rowMin > threshold) return threshold + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static bool RectsOverlapEnough(System.Windows.Rect a, System.Windows.Rect b, double minRatio)
    {
        var ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (ix <= 0 || iy <= 0) return false;
        var intersection = ix * iy;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 && intersection / smaller >= minRatio;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? string.Empty) : s.Substring(0, max) + "…";
}
