using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Wires CaptureService → OcrService → TranslationService → OverlayManager
/// into a single start/stop translation pipeline.
/// </summary>
public class TranslationEngine : IDisposable
{
    private readonly CaptureService _capture;
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly OverlayManager _overlay;
    private readonly AppSettings _settings;
    private readonly HistoryRepository _history;
    private readonly ProfileManager _profiles;
    private readonly ILogger _logger;
    private int _processingFlag;
    private bool _disposed;
    private long _lastFrameHash;

    // Rolling average of "frame received → overlay updated" wall-clock time.
    // Cheap and useful as a single throughput indicator: covers OCR + grouping
    // + translation + overlay layout. Smoothed with EMA to avoid one slow
    // frame whip-sawing the readout.
    private double _avgFrameMs;
    private long _lastFrameMs;

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
        List<TranslationRegion> Translated);

    // v1.0.26: output-side dedup. v1.0.25's input-side frame reuse skips
    // the pipeline entirely when raw regions match a recent frame
    // closely enough — but jitter on edge regions sometimes prevents the
    // strict per-region match, so the pipeline runs and produces a
    // translation that's visually different from what's already on
    // screen even though the underlying SOURCE CONTENT is identical
    // (just regrouped). To stop that residual flicker, we compute a
    // position-agnostic, normalised fingerprint of the source words
    // covered each frame; if the fingerprint matches what's currently
    // rendered, we keep the existing overlay instead of redrawing the
    // newly-translated (but functionally equivalent) version.
    private string? _displayedSceneFingerprint;

    public bool IsRunning { get; private set; }
    public IntPtr TargetWindowHandle { get; private set; }
    public string TargetWindowTitle { get; private set; } = string.Empty;

    /// <summary>
    /// True when the user explicitly paused the engine (via hotkey or
    /// menu). Different from <see cref="CaptureService.IsPaused"/> which
    /// pauses automatically when the target window is iconic — they
    /// compose: a user-paused + iconic window stays paused, and resuming
    /// only flips the user flag.
    /// </summary>
    public bool IsUserPaused { get; private set; }

    /// <summary>Last completed frame's end-to-end processing time in ms (0 if none yet).</summary>
    public long LastFrameMs => _lastFrameMs;

    /// <summary>Exponentially-smoothed average frame time in ms (0 if no samples yet).</summary>
    public double AverageFrameMs => _avgFrameMs;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? StatusUpdated;
    /// <summary>Raised when <see cref="IsUserPaused"/> flips. Tray + main VM listen so the icon and status text stay accurate.</summary>
    public event EventHandler<bool>? UserPauseChanged;

    public TranslationEngine(
        CaptureService capture,
        OcrService ocr,
        TranslationService translation,
        OverlayManager overlay,
        AppSettings settings,
        HistoryRepository history,
        ProfileManager profiles,
        ILogger logger)
    {
        _capture = capture;
        _ocr = ocr;
        _translation = translation;
        _overlay = overlay;
        _settings = settings;
        _history = history;
        _profiles = profiles;
        _logger = logger;

        _capture.FrameCaptured += OnFrameCaptured;
        _capture.PauseStateChanged += OnCapturePauseChanged;
        _translation.FallbackStateChanged += OnFallbackStateChanged;
    }

    private void OnFallbackStateChanged(object? sender, string message)
    {
        // Surface the switch in the main status line so the user knows
        // why the active provider name suddenly changed in the tooltip.
        // Message is already localised (TranslationService formats it
        // through LanguageManager.Format).
        if (IsRunning) StatusUpdated?.Invoke(this, message);
    }

    private void OnCapturePauseChanged(object? sender, bool isPaused)
    {
        if (!IsRunning) return;
        // The capture loop stops feeding us frames while the window is
        // iconic, but the overlay is its own WPF window — without an
        // explicit Hide() it sticks around showing the last translation
        // until the next frame arrives. Race-y in particular at the
        // moment of minimisation: an in-flight frame can sneak through
        // and reposition the overlay to (-32000,-32000), which is why
        // a second minimise "fixed" the visual but left the bug latent.
        if (isPaused) _overlay.Hide();
        StatusUpdated?.Invoke(this, isPaused
            ? LanguageManager.Format("Strings.Engine.Paused", TargetWindowTitle)
            : LanguageManager.Format("Strings.Engine.Active", TargetWindowTitle));
    }

    public async Task StartAsync(IntPtr hwnd, string title, string processName = "")
    {
        if (IsRunning)
            await StopAsync();

        TargetWindowHandle = hwnd;
        TargetWindowTitle = title;

        // Pick + apply the right profile for this window BEFORE we kick off
        // any backend so OCR/translator/overlay all read the right settings.
        // GetOrCreate auto-mints a per-process profile when nothing matched
        // and we have a sensible process name — gives the user a "settings
        // remembered per-game" experience without any UI work on their part.
        var profile = _profiles.GetOrCreateForWindow(title, processName);
        _profiles.ApplyProfile(profile);
        // Reload backends — language/engine/provider could all have changed.
        _ocr.Reload();
        _translation.Reload();

        if (!_translation.IsReady)
        {
            if (!_translation.Initialize())
            {
                StatusUpdated?.Invoke(this, LanguageManager.Get("Strings.Engine.NoTranslator"));
                return;
            }
        }

        _settings.Config.TargetWindowHandle = hwnd;
        _settings.Config.TargetWindowTitle = title;
        _settings.Save();

        _history.StartSession(title);
        await _capture.StartCaptureAsync(hwnd);
        IsRunning = true;
        StatusUpdated?.Invoke(this,
            profile.IsDefault
                ? LanguageManager.Format("Strings.Engine.Active", title)
                : LanguageManager.Format("Strings.Engine.ActiveWithProfile", title, profile.Name));
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("Engine started for {Title} (profile: {Profile})", title, profile.Name);
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        await _capture.StopCaptureAsync();
        _overlay.Hide();
        // Clear the jitter-stabilisation memory — next Start might be on a
        // completely different game/scene, no point biasing it with last
        // session's text fragments.
        lock (_recentRegionsLock) _recentRegions.Clear();
        // Same reasoning for the grouping-hysteresis memory: a new scene
        // shouldn't inherit last session's merge decisions.
        lock (_recentGroupingsLock) _recentGroupings.Clear();
        // Same for the v1.0.25 full-frame reuse snapshot — the next Start
        // is on a fresh window and shouldn't reuse the previous game's
        // translations even if positions happened to overlap.
        lock (_lastCompletedFrameLock) _lastCompletedFrame = null;
        // And the v1.0.26 displayed-scene fingerprint — without this, the
        // next Start's first frame would falsely match the old session's
        // last fingerprint and skip its own overlay update.
        _displayedSceneFingerprint = null;
        _history.EndSession(
            _settings.Config.CharactersTranslatedToday,
            _settings.Config.CacheHits + _settings.Config.CacheMisses);
        IsRunning = false;
        // Clear user-pause on stop so the next Start doesn't silently
        // inherit it and leave the user wondering why nothing's happening.
        if (IsUserPaused)
        {
            IsUserPaused = false;
            UserPauseChanged?.Invoke(this, false);
        }
        StatusUpdated?.Invoke(this, LanguageManager.Get("Strings.Engine.Stopped"));
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("Engine stopped");
    }

    public void ToggleOverlay()
    {
        if (_overlay.IsVisible)
            _overlay.Hide();
        else
            _overlay.UpdatePosition(TargetWindowHandle);
    }

    /// <summary>
    /// Toggle the user-pause flag. While paused, OnFrameCaptured drops
    /// frames immediately (preserving the OCR backend, LLM context, profile
    /// and stats — only the per-frame work is suspended), and the overlay
    /// is hidden so it doesn't display a stale translation indefinitely.
    /// No-op when the engine is stopped — there's nothing to pause.
    /// </summary>
    public void TogglePause()
    {
        if (!IsRunning) return;
        IsUserPaused = !IsUserPaused;
        if (IsUserPaused)
        {
            _overlay.Hide();
            StatusUpdated?.Invoke(this, LanguageManager.Format("Strings.Engine.UserPaused", TargetWindowTitle));
        }
        else
        {
            // Reset the dedup hash so the very next frame is treated as
            // novel — otherwise an unchanged scene would be skipped and
            // the user would see no overlay until something moves.
            _lastFrameHash = 0;
            // Reproduce the same status string StartAsync emits — profile
            // name is shown when one's active, plain title otherwise.
            var profile = _profiles.ActiveProfile;
            StatusUpdated?.Invoke(this, profile.IsDefault
                ? LanguageManager.Format("Strings.Engine.Active", TargetWindowTitle)
                : LanguageManager.Format("Strings.Engine.ActiveWithProfile", TargetWindowTitle, profile.Name));
        }
        UserPauseChanged?.Invoke(this, IsUserPaused);
    }

    private async void OnFrameCaptured(object? sender, Bitmap bitmap)
    {
        // User-pause early-out: throw the frame away without taking the
        // single-flight slot, so the moment the user resumes the next
        // frame can land immediately. No state mutation here — capture
        // service keeps polling, OCR/LLM/overlay just skip work.
        if (IsUserPaused)
        {
            bitmap.Dispose();
            return;
        }

        // Single-flight: drop frames if previous still being processed.
        if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
        {
            bitmap.Dispose();
            return;
        }

        var sw = Stopwatch.StartNew();
        var didWork = false;
        try
        {
            // Sample-based hash of the captured pixels: lets us skip the OCR +
            // translation pipeline entirely when the game is on a static screen.
            // Cheap (4096 pixel reads), deterministic, and good enough to
            // distinguish "still on the same dialog" from "scene changed".
            var hash = QuickSampleHash(bitmap);
            if (hash == _lastFrameHash)
            {
                bitmap.Dispose();
                return;
            }
            _lastFrameHash = hash;
            didWork = true;

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                bytes = ms.ToArray();
            }
            bitmap.Dispose();

            var rawRegions = _ocr.ProcessFrame(bytes);
            _logger.Debug("Frame: OCR -> {Count} raw regions", rawRegions.Count);
            if (rawRegions.Count == 0) return;

            // v1.0.25: per-frame reuse short-circuit. If the new raw OCR
            // output is essentially identical to the previous frame's
            // (every new region matches a previous one by position +
            // text within OCR-jitter tolerance), skip the entire
            // grouping + translation pipeline and just re-render the
            // previous translated regions over the new window rect.
            // This is what stops the merged-vs-split flicker: when the
            // source hasn't meaningfully changed, we literally stop
            // recomputing the output.
            if (TryReusePreviousFrame(rawRegions, out var winRectShortcut, out var reusedRegions))
            {
                _overlay.ShowRegions(reusedRegions!, winRectShortcut);
                return;
            }

            // Stitch adjacent lines of the same paragraph back together so a
            // dialog that wraps to N lines is translated as one sentence
            // instead of N independent fragments. v1.0.23: try the
            // grouping-hysteresis path first — if the current raw layout
            // matches a recent frame's, replay that frame's merge
            // decision instead of relitigating with the gap heuristic.
            // Otherwise run the regular Group + memorise its decision.
            var regions = ApplyGroupingHysteresisOrGroup(rawRegions);
            if (regions.Count != rawRegions.Count)
                _logger.Debug("Frame: grouped {From} -> {To} regions", rawRegions.Count, regions.Count);

            // v1.0.19: snap each region's source text to its near-identical
            // neighbour from a recent frame — kills OCR jitter that would
            // otherwise miss the cache and produce visibly-different
            // translations of essentially the same line.
            StabiliseAgainstRecent(regions);
            foreach (var r in regions)
                _logger.Debug("  -> tx[{Y:F0},{H:F0}]: '{Text}'",
                    r.Bounds.Top, r.Bounds.Height,
                    r.OriginalText.Length > 80 ? r.OriginalText.Substring(0, 80) + "..." : r.OriginalText);

            // Resolve target window rect ONCE up-front so the streaming
            // callback can re-render the overlay without repeatedly calling
            // back into Win32. If the window vanished between capture and
            // here, bail before kicking off any translation work.
            if (!GetWindowRect(TargetWindowHandle, out var rect))
            {
                _logger.Warning("Frame: GetWindowRect failed for handle {Handle}", TargetWindowHandle);
                return;
            }
            var winRect = new System.Windows.Rect(
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

            // For streaming-capable LLM providers (OpenAI / Anthropic with
            // UseStreamingLlm = true) the service feeds us partial text per
            // region as the SSE stream arrives. We re-render the overlay
            // each time so the user sees the translation appear word-by-
            // word — TTFT ~200 ms vs the ~1-2 s of a full round-trip.
            var partial = new List<TranslationRegion>();
            void OnRegionUpdated(TranslationRegion r)
            {
                if (string.IsNullOrEmpty(r.TranslatedText)) return;
                if (!partial.Contains(r)) partial.Add(r);
                _overlay.ShowRegions(partial, winRect);
            }

            var translated = await _translation.TranslateRegionsAsync(
                regions, _settings.Config.TargetLanguage, OnRegionUpdated);
            _logger.Debug("Frame: Translation -> {Count} regions", translated.Count);
            if (translated.Count == 0) return;

            // Remember what we translated this frame so the NEXT frame's
            // jitter-stabilisation pass can find it. Done after translation
            // (not before) so we only memorise text that actually produced
            // a translation.
            RememberRecentRegions(translated);

            // v1.0.26: output-side dedup. If the source content of this
            // frame matches what's already rendered (same English words
            // covering roughly the same area, just regrouped differently
            // by jitter), keep the existing overlay verbatim — including
            // its translation. The pipeline still ran (we don't pay any
            // perf savings here, that's v1.0.25's job), but the overlay
            // doesn't churn so the user sees stable text.
            //
            // We DO update the FrameSnapshot's raw bounds with this
            // frame's positions so the next call's v1.0.25 reuse layer
            // can match the latest layout — but the Translated payload
            // stays the OLD one (= what's actually displayed), keeping
            // input-side reuse and on-screen content in sync.
            var sceneFp = ComputeSceneFingerprint(rawRegions);
            FrameSnapshot? prevFrame;
            lock (_lastCompletedFrameLock) prevFrame = _lastCompletedFrame;
            if (sceneFp == _displayedSceneFingerprint && prevFrame != null)
            {
                _logger.Debug("Frame: same source fingerprint as displayed — overlay kept");
                RememberCompletedFrame(rawRegions, prevFrame.Translated);
                return;
            }
            _displayedSceneFingerprint = sceneFp;

            // v1.0.25: store this frame's full snapshot so the next frame's
            // reuse-shortcut at the top of OnFrameCaptured can skip the
            // pipeline entirely if the OCR output matches.
            RememberCompletedFrame(rawRegions, translated);

            // Final render: covers the non-streaming case (callback never
            // fired) AND ensures any post-stream glossary substitution lands
            // even if it produced an identical fingerprint to the last
            // partial chunk.
            _overlay.ShowRegions(translated, winRect);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Engine frame processing error");
        }
        finally
        {
            // Only count frames that actually went through OCR — early-outs
            // (cache hash hit on static screen) would otherwise drag the
            // average to near-zero and hide the real cost.
            if (didWork)
            {
                _lastFrameMs = sw.ElapsedMilliseconds;
                // EMA with α=0.2 — feels responsive without being jumpy.
                _avgFrameMs = _avgFrameMs == 0
                    ? _lastFrameMs
                    : _avgFrameMs * 0.8 + _lastFrameMs * 0.2;
            }
            Interlocked.Exchange(ref _processingFlag, 0);
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
    /// </summary>
    private bool TryReusePreviousFrame(
        List<TranslationRegion> newRawRegions,
        out System.Windows.Rect winRect,
        out List<TranslationRegion>? reused)
    {
        winRect = default;
        reused = null;

        FrameSnapshot? snap;
        lock (_lastCompletedFrameLock) snap = _lastCompletedFrame;
        if (snap == null) return false;
        if (newRawRegions.Count == 0) return false;

        // Resolve target window rect — needed regardless because the
        // window may have moved since last frame. If we can't get it,
        // fall through to the normal pipeline (which has its own bail).
        if (!GetWindowRect(TargetWindowHandle, out var rect)) return false;
        winRect = new System.Windows.Rect(
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

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

        reused = snap.Translated;
        _logger.Debug("Frame: reused previous translation ({N} regions)", reused.Count);
        return true;
    }

    /// <summary>
    /// Position-agnostic content fingerprint of the frame. Lowercase + strip
    /// non-alphanumerics from each region's source text, sort the resulting
    /// tokens, and join — so a 3-line dialogue split and merged differently
    /// across two frames produces the SAME fingerprint as long as the
    /// underlying English words are the same. Tiny fragments
    /// (&lt; 3 chars after normalisation) are discarded as noise so a
    /// vanishing/appearing single character doesn't change the result.
    /// Used by v1.0.26 to suppress overlay redraws when the on-screen
    /// content is functionally unchanged.
    /// </summary>
    private static string ComputeSceneFingerprint(List<TranslationRegion> regions)
    {
        var tokens = new List<string>(regions.Count);
        foreach (var r in regions)
        {
            var norm = NormaliseForFingerprint(r.OriginalText);
            if (norm.Length >= 3) tokens.Add(norm);
        }
        tokens.Sort(StringComparer.Ordinal);
        return string.Join("|", tokens);
    }

    private static string NormaliseForFingerprint(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
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
    private void RememberCompletedFrame(List<TranslationRegion> rawRegions, List<TranslationRegion> translated)
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
            _lastCompletedFrame = new FrameSnapshot(bounds, texts, translatedCopy);
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
    private List<TranslationRegion> ApplyGroupingHysteresisOrGroup(List<TranslationRegion> rawRegions)
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
    private void StabiliseAgainstRecent(List<TranslationRegion> regions)
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
    private void RememberRecentRegions(List<TranslationRegion> regions)
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

    /// <summary>
    /// Sample 64x64 grid of pixels from the bitmap and combine into a long.
    /// FNV-1a-ish; not cryptographic, just stable enough to detect "frame
    /// pixels are identical to last time".
    /// </summary>
    private static long QuickSampleHash(Bitmap bmp)
    {
        const int samples = 64;
        long hash = unchecked((long)0xcbf29ce484222325UL);
        var w = bmp.Width;
        var h = bmp.Height;
        for (int sy = 0; sy < samples; sy++)
        {
            int y = (int)((sy + 0.5) / samples * h);
            for (int sx = 0; sx < samples; sx++)
            {
                int x = (int)((sx + 0.5) / samples * w);
                hash ^= bmp.GetPixel(x, y).ToArgb();
                hash *= unchecked((long)0x100000001b3L);
            }
        }
        return hash;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public void Dispose()
    {
        if (_disposed) return;
        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.PauseStateChanged -= OnCapturePauseChanged;
        _translation.FallbackStateChanged -= OnFallbackStateChanged;
        if (IsRunning)
            StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
