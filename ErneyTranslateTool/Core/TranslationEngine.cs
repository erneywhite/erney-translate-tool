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

    // Grouping hysteresis, jitter stabilisation and frame reuse (v1.0.35:
    // moved to FrameLayout so recorded frames can be replayed in tests).
    private readonly FrameLayout _layout;
    private readonly OcrRecorder _recorder;
    private volatile bool _recordOcr;

    /// <summary>
    /// Diagnostics: write every OCR'd frame to logs/ocr-recordings (see
    /// OcrRecorder). Not saved in settings: left on by accident it would
    /// grow a file for as long as the app runs. A file covers one run of
    /// the engine, from the first frame to Stop or to switching this off.
    /// </summary>
    public string OcrRecordingsFolder => _recorder.Directory;

    /// <summary>
    /// Snapshot translations go into the same recording when it is on, so a
    /// page that came out wrong can be replayed too.
    /// </summary>
    public void RecordSnapshotFrame(IReadOnlyList<TranslationRegion> raw, int width, int height, string windowTitle)
    {
        if (!_recordOcr) return;
        if (!_recorder.IsRecording)
            _recorder.Start(windowTitle, _settings.Config.OcrEngine, _settings.Config.SourceLanguage);
        _recorder.Record(raw, width, height);
    }

    public bool RecordOcr
    {
        get => _recordOcr;
        set
        {
            _recordOcr = value;
            if (!value) _recorder.Stop();
        }
    }

    // v1.0.33: frames are processed on a worker thread (CaptureService runs
    // its loop via Task.Run). A frame still translating when the user hits
    // Stop finishes later; the generation stamp makes it drop its result
    // instead of re-showing the overlay or poisoning the next session's
    // reuse snapshot, and the token cancels its network calls.
    private int _runGeneration;
    private CancellationTokenSource _runCts = new();
    private int _threadLoggedForGeneration = -1;

    // v1.0.30: vanished-text handling. When the text the overlay shows is
    // gone from the screen (dialog closed, CG shown), the overlay used to
    // keep the stale translation until new text appeared. Now it's dropped
    // once the text has been missing for VanishConfirmFrames ticks in a row
    // — one tick is not enough, OCR occasionally misses a line on a single
    // frame. While a vanish is pending (0 < streak < confirm) a frame with
    // an unchanged pixel hash is re-processed instead of skipped, otherwise
    // a static screen would never produce the confirming tick.
    private const int VanishConfirmFrames = 2;
    private int _vanishStreak;
    private bool VanishPending => _vanishStreak > 0 && _vanishStreak < VanishConfirmFrames;

    // Rolling average of "frame received → overlay updated" wall-clock time.
    // Cheap and useful as a single throughput indicator: covers OCR + grouping
    // + translation + overlay layout. Smoothed with EMA to avoid one slow
    // frame whip-sawing the readout.
    private double _avgFrameMs;
    private long _lastFrameMs;

    // v1.0.27 NOTE: v1.0.26 introduced an output-side scene-fingerprint
    // dedup that was meant to skip overlay updates when the pipeline
    // produced a "functionally equivalent" translation. In practice it
    // (a) didn't catch the merge-vs-split case it was designed for —
    // the fingerprint tokenised per-region instead of per-word, so
    // splitting one region into two changed the fingerprint — AND
    // (b) interacted badly with streaming: the streaming partial
    // callbacks already updated the overlay during translation, then
    // the dedup at end of pipeline would sometimes save the OLD
    // translated regions to the snapshot, and the next frame's
    // input-side reuse would render OLD over the user's just-seen NEW.
    // Visible bug: translations appearing then disappearing for ~10 sec
    // before showing again. Reverted; only the v1.0.25 input-side reuse
    // remains.

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

    /// <summary>Show a line in the main status (the snapshot translation reports through here).</summary>
    public void ReportStatus(string message) => StatusUpdated?.Invoke(this, message);
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
        OcrRecorder recorder,
        ILogger logger)
    {
        _capture = capture;
        _ocr = ocr;
        _translation = translation;
        _overlay = overlay;
        _settings = settings;
        _history = history;
        _profiles = profiles;
        _recorder = recorder;
        _logger = logger;
        _layout = new FrameLayout(logger);

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
        _overlay.TargetWindow = hwnd;

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
        Interlocked.Increment(ref _runGeneration);
        _runCts = new CancellationTokenSource();
        // The last frame of the previous run is still remembered: on a static
        // screen the first frame of this run matched it and was skipped, and
        // so was every frame after — Stop → Start never translated anything.
        Interlocked.Exchange(ref _lastFrameHash, 0);
        _vanishStreak = 0;
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
        Interlocked.Increment(ref _runGeneration);
        _runCts.Cancel();
        await _capture.StopCaptureAsync();
        _recorder.Stop();
        _overlay.Hide();
        // Clear the jitter-stabilisation memory — next Start might be on a
        // completely different game/scene, no point biasing it with last
        // session's text fragments.
        _layout.Reset();
        lock (_shownLock)
        {
            _shown = new();
            _vanishedAt.Clear();
        }
        // (and the grouping-hysteresis memory and the reuse snapshot —
        // FrameLayout.Reset clears all three).
        _vanishStreak = 0;
        // A fresh Start shows the overlay again even if it was hidden.
        _overlay.HiddenByUser = false;
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

    /// <summary>
    /// Overlay style changed in settings: re-render on the next tick even
    /// on a static screen, where unchanged frames are otherwise skipped.
    /// The frame then goes through reuse, and OverlayWindow rebuilds the
    /// labels because its style fingerprint changed.
    /// </summary>
    public void RequestOverlayRestyle()
    {
        if (IsRunning) Interlocked.Exchange(ref _lastFrameHash, 0);
    }

    /// <summary>True while the user has hidden the overlay with the button or hotkey.</summary>
    public bool IsOverlayHiddenByUser => _overlay.HiddenByUser;

    /// <summary>
    /// Hide / show the overlay on user request. v1.0.31: hiding is sticky
    /// (see <see cref="OverlayManager.HiddenByUser"/>), and showing actually
    /// shows it: the old "show" branch only moved the window without calling
    /// Show(), so the overlay came back only when new text appeared — never
    /// on a static screen, where unchanged frames are skipped. Now the frame
    /// hash is reset so the very next tick re-renders.
    /// </summary>
    public void ToggleOverlay()
    {
        _overlay.HiddenByUser = !_overlay.HiddenByUser;
        if (_overlay.HiddenByUser)
            _overlay.Hide();
        else
            Interlocked.Exchange(ref _lastFrameHash, 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
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
            Interlocked.Exchange(ref _lastFrameHash, 0);
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

        // v1.0.31: OCR still loading (Start reloads the backend; PaddleOCR
        // takes seconds to bring its models up) — drop the frame WITHOUT
        // recording its hash. Before, the "0 regions" result of a not-ready
        // backend got the frame hash remembered, and on a static screen every
        // later frame matched it and was skipped: translation never started
        // until something on screen moved.
        if (_ocr.State != OcrBackendState.Ready)
        {
            bitmap.Dispose();
            return;
        }

        // Single-flight: drop frames if previous still being processed.
        if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
        {
            // v1.0.39: a dropped frame still tells whether shown text left.
            QuickVanishCheck(bitmap);
            bitmap.Dispose();
            return;
        }

        _pipelineFrameTime = DateTime.UtcNow;
        var generation = Volatile.Read(ref _runGeneration);
        var ct = _runCts.Token;
        if (Interlocked.Exchange(ref _threadLoggedForGeneration, generation) != generation)
            _logger.Debug("Frame pipeline thread {Id}, UI thread: {Ui}",
                Environment.CurrentManagedThreadId,
                System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false);
        var sw = Stopwatch.StartNew();
        var didWork = false;
        try
        {
            // Sample-based hash of the captured pixels: lets us skip the OCR +
            // translation pipeline entirely when the game is on a static screen.
            // Cheap (4096 pixel reads), deterministic, and good enough to
            // distinguish "still on the same dialog" from "scene changed".
            var hash = QuickSampleHash(bitmap);
            if (hash == _lastFrameHash && !VanishPending)
            {
                bitmap.Dispose();
                FollowWindowMove();
                return;
            }
            _lastFrameHash = hash;
            didWork = true;

            var (frameWidth, frameHeight) = (bitmap.Width, bitmap.Height);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                bytes = ms.ToArray();
            }
            // v1.0.35: the frame stays alive until the blocks are grouped —
            // ColorSampler reads their colours from it. Disposed in finally.

            var rawRegions = _ocr.ProcessFrame(bytes);
            _logger.Debug("Frame: OCR -> {Count} raw regions", rawRegions.Count);
            if (_recordOcr)
            {
                if (!_recorder.IsRecording)
                    _recorder.Start(TargetWindowTitle, _settings.Config.OcrEngine, _settings.Config.SourceLanguage);
                _recorder.Record(rawRegions, frameWidth, frameHeight);
            }
            // After recording, so recordings keep what OCR really saw.
            var noise = OcrTextHelpers.DropNoise(rawRegions);
            if (noise > 0) _logger.Debug("Frame: dropped {N} noise regions", noise);
            var logos = OcrTextHelpers.DropOversized(rawRegions);
            if (logos > 0) _logger.Debug("Frame: left {N} oversized lines (logos, titles) untranslated", logos);
            if (rawRegions.Count == 0)
            {
                if (++_vanishStreak >= VanishConfirmFrames)
                {
                    if (_vanishStreak == VanishConfirmFrames)
                        _logger.Debug("Frame: text gone for {N} ticks, hiding overlay", _vanishStreak);
                    HideIfCurrent(generation);
                }
                return;
            }

            // v1.0.25: per-frame reuse short-circuit. If the new raw OCR
            // output is essentially identical to the previous frame's
            // (every new region matches a previous one by position +
            // text within OCR-jitter tolerance), skip the entire
            // grouping + translation pipeline and just re-render the
            // previous translated regions over the new window rect.
            // This is what stops the merged-vs-split flicker: when the
            // source hasn't meaningfully changed, we literally stop
            // recomputing the output.
            // Pixels are copied only if a block went missing and is checked.
            ColorSampler.Pixels? framePixels = null;
            bool IsBlank(TranslationRegion block)
            {
                framePixels ??= ColorSampler.Snapshot(bitmap);
                return ColorSampler.TextGone(framePixels.Value, block);
            }

            if (_layout.TryReusePreviousFrame(rawRegions, FrameLayout.BlockVanishFrames, out var reusedRegions, IsBlank)
                && TryGetTargetRect(out var winRectShortcut))
            {
                // Blocks whose text left the screen are dropped per block,
                // after FrameLayout.BlockVanishFrames misses (FrameLayout counts them).
                _vanishStreak = 0;
                ShowIfCurrent(generation, reusedRegions!, winRectShortcut);
                return;
            }
            _vanishStreak = 0;

            // Stitch adjacent lines of the same paragraph back together so a
            // dialog that wraps to N lines is translated as one sentence
            // instead of N independent fragments. v1.0.23: try the
            // grouping-hysteresis path first — if the current raw layout
            // matches a recent frame's, replay that frame's merge
            // decision instead of relitigating with the gap heuristic.
            // Otherwise run the regular Group + memorise its decision.
            var regions = _layout.ApplyGroupingHysteresisOrGroup(rawRegions);
            if (regions.Count != rawRegions.Count)
                _logger.Debug("Frame: grouped {From} -> {To} regions", rawRegions.Count, regions.Count);

            // v1.0.19: snap each region's source text to its near-identical
            // neighbour from a recent frame — kills OCR jitter that would
            // otherwise miss the cache and produce visibly-different
            // translations of essentially the same line.
            _layout.StabiliseAgainstRecent(regions);
            try
            {
                ColorSampler.Sample(bitmap, regions, rawRegions);
            }
            catch (Exception ex)
            {
                // The overlay falls back to the plate colours.
                _logger.Debug(ex, "Frame: colour sampling failed");
            }
            foreach (var r in regions)
                _logger.Debug("  -> tx[{Y:F0},{H:F0}]: '{Text}'",
                    r.Bounds.Top, r.Bounds.Height,
                    r.OriginalText.Length > 80 ? r.OriginalText.Substring(0, 80) + "..." : r.OriginalText);

            // Resolve target window rect ONCE up-front so the streaming
            // callback can re-render the overlay without repeatedly calling
            // back into Win32. If the window vanished between capture and
            // here, bail before kicking off any translation work.
            if (!TryGetTargetRect(out var winRect))
            {
                _logger.Warning("Frame: window rect unavailable for handle {Handle}", TargetWindowHandle);
                return;
            }

            // For streaming-capable LLM providers (OpenAI / Anthropic with
            // UseStreamingLlm = true) the service feeds us partial text per
            // region as the SSE stream arrives. We re-render the overlay
            // each time so the user sees the translation appear word-by-
            // word — TTFT ~200 ms vs the ~1-2 s of a full round-trip.
            // v1.0.35: blocks are translated in parallel, so this is called
            // from several threads; each render gets its own copy of the list.
            var partial = new List<TranslationRegion>();
            void OnRegionUpdated(TranslationRegion r)
            {
                if (string.IsNullOrEmpty(r.TranslatedText)) return;
                List<TranslationRegion> snapshot;
                lock (partial)
                {
                    if (!partial.Contains(r)) partial.Add(r);
                    snapshot = new List<TranslationRegion>(partial);
                }
                ShowIfCurrent(generation, snapshot, winRect, isPartial: true);
            }

            var translated = await _translation.TranslateRegionsAsync(
                regions, _settings.Config.TargetLanguage, OnRegionUpdated, ct);
            _logger.Debug("Frame: Translation -> {Count} regions", translated.Count);
            if (translated.Count == 0) return;
            // Stopped (or restarted) while we were waiting on the network.
            if (generation != Volatile.Read(ref _runGeneration)) return;

            // Remember what we translated this frame so the NEXT frame's
            // jitter-stabilisation pass can find it. Done after translation
            // (not before) so we only memorise text that actually produced
            // a translation.
            _layout.RememberRecentRegions(translated);

            // v1.0.35: a block OCR missed for just this frame stays up.
            translated = _layout.HoldBriefDropouts(translated, FrameLayout.BlockVanishFrames, IsBlank);

            // v1.0.25: also store this frame's full snapshot so the next
            // frame's reuse-shortcut at the top of OnFrameCaptured can
            // skip the pipeline entirely if the OCR output matches.
            _layout.RememberCompletedFrame(rawRegions, translated);

            // Final render: covers the non-streaming case (callback never
            // fired) AND ensures any post-stream glossary substitution lands
            // even if it produced an identical fingerprint to the last
            // partial chunk.
            ShowIfCurrent(generation, translated, winRect);
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
            bitmap.Dispose(); // safe if an early exit already disposed it
            Interlocked.Exchange(ref _processingFlag, 0);
        }
    }

    private void ShowIfCurrent(int generation, IReadOnlyList<TranslationRegion> regions,
        System.Windows.Rect winRect, bool isPartial = false)
    {
        // Checked twice: here to skip the dispatcher round-trip, and again on
        // the UI thread inside ShowRegions, where Stop / Pause / Hide run —
        // that second check is the one that closes the race.
        bool StillWanted() => generation == Volatile.Read(ref _runGeneration) && !IsUserPaused;
        _lastOverlayRect = winRect;
        regions = WithoutVanished(regions);
        if (!isPartial)
            lock (_shownLock)
            {
                _shown = regions.ToList();
                _shownRect = winRect;
            }
        if (StillWanted())
            _overlay.ShowRegions(regions, winRect, isPartial, StillWanted);
    }

    // ---- v1.0.39: quick vanish check on dropped frames ----
    //
    // A frame is processed only after the previous one is done (OCR at full
    // size plus translation: a second or more), and every frame captured in
    // between was thrown away, so text that left the screen was noticed late.
    // Dropped frames are now checked against the blocks on screen: if a
    // block's letters are gone from its place (ColorSampler.TextGone, pixels
    // only, a few ms), it is taken off right away. A render from a frame
    // captured before that moment doesn't bring it back.

    private readonly object _shownLock = new();
    private List<TranslationRegion> _shown = new();
    private System.Windows.Rect _shownRect;
    private readonly Dictionary<string, DateTime> _vanishedAt = new(StringComparer.Ordinal);
    private DateTime _pipelineFrameTime;
    private int _quickCheckBusy;

    private void QuickVanishCheck(Bitmap frame)
    {
        if (IsUserPaused || Interlocked.Exchange(ref _quickCheckBusy, 1) == 1) return;
        try
        {
            List<TranslationRegion> shown;
            System.Windows.Rect rect;
            lock (_shownLock)
            {
                shown = _shown;
                rect = _shownRect;
            }
            if (!shown.Any(r => r.SampledText != null && r.TextPixelShare > 0.002)) return;

            var pixels = ColorSampler.Snapshot(frame);
            var gone = shown.Where(r => r.SampledText != null && ColorSampler.TextGone(pixels, r)).ToList();
            if (gone.Count == 0) return;

            var now = DateTime.UtcNow;
            lock (_shownLock)
                foreach (var g in gone) _vanishedAt[g.OriginalText] = now;
            _logger.Debug("Quick check: {N} block(s) left the screen", gone.Count);
            ShowIfCurrent(Volatile.Read(ref _runGeneration), shown.Except(gone).ToList(), rect);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Quick vanish check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _quickCheckBusy, 0);
        }
    }

    /// <summary>
    /// Drop blocks the quick check saw leave after the frame being rendered
    /// was captured; forget the ones a newer frame has already judged.
    /// </summary>
    private IReadOnlyList<TranslationRegion> WithoutVanished(IReadOnlyList<TranslationRegion> regions)
    {
        lock (_shownLock)
        {
            if (_vanishedAt.Count == 0) return regions;
            var frameTime = _pipelineFrameTime;
            foreach (var key in _vanishedAt.Where(kv => kv.Value < frameTime).Select(kv => kv.Key).ToList())
                _vanishedAt.Remove(key);
            return _vanishedAt.Count == 0 ? regions : regions.Where(r => !_vanishedAt.ContainsKey(r.OriginalText)).ToList();
        }
    }

    private void HideIfCurrent(int generation)
    {
        if (generation == Volatile.Read(ref _runGeneration))
        {
            lock (_shownLock) _shown = new();
            _overlay.Hide();
        }
    }

    private System.Windows.Rect _lastOverlayRect;

    /// <summary>
    /// v1.0.35: the overlay is positioned when a frame is rendered, but a
    /// window dragged over a static scene produces only hash-skipped frames,
    /// and the translation stayed where the window used to be.
    /// </summary>
    private void FollowWindowMove()
    {
        if (!TryGetTargetRect(out var rect) || rect == _lastOverlayRect) return;
        _lastOverlayRect = rect;
        _overlay.UpdatePosition(TargetWindowHandle);
    }

    /// <summary>Current target window rect in screen pixels; false if the window is gone.</summary>
    private bool TryGetTargetRect(out System.Windows.Rect winRect) =>
        // v1.0.35: the client area — that's what CaptureService crops to.
        WindowGeometry.TryGetClientRect(TargetWindowHandle, out winRect);

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

    public void Dispose()
    {
        if (_disposed) return;
        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.PauseStateChanged -= OnCapturePauseChanged;
        _translation.FallbackStateChanged -= OnFallbackStateChanged;
        // v1.0.33: never block here. Dispose runs on the UI thread from
        // App.OnExit; waiting synchronously for the capture loop could
        // deadlock against a frame that needs the dispatcher. MainWindow's
        // exit path awaits StopAsync() before Shutdown, so normally we're
        // already stopped; otherwise just cancel and record the session.
        if (IsRunning)
        {
            Interlocked.Increment(ref _runGeneration);
            _runCts.Cancel();
            _ = _capture.StopCaptureAsync();
            IsRunning = false;
            _history.EndSession(
                _settings.Config.CharactersTranslatedToday,
                _settings.Config.CacheHits + _settings.Config.CacheMisses);
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
