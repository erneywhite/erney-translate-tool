using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// v1.0.37 "translate a snapshot": one hotkey press reads the window once,
/// translates it and leaves the translation frozen over the game; the next
/// press takes it away. Like the camera in Google Translate. For screens
/// where continuous translation can't settle (cards that move, tickers,
/// animated menus) and for a quick look without starting anything.
///
/// Works without Start: it takes the window picked on the main tab, or the
/// active window if none is picked. While continuous translation runs, the
/// snapshot pauses it and the second press resumes it.
/// </summary>
public sealed class SnapshotService
{
    private readonly CaptureService _capture;
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly OverlayManager _overlay;
    private readonly ProfileManager _profiles;
    private readonly AppSettings _settings;
    private readonly TranslationEngine _engine;
    private readonly WindowPickerService _windows;
    private readonly ILogger _logger;

    private volatile bool _shown;
    private int _busy;
    private int _generation;
    private bool _pausedEngine;

    public SnapshotService(CaptureService capture, OcrService ocr, TranslationService translation,
        OverlayManager overlay, ProfileManager profiles, AppSettings settings, TranslationEngine engine,
        WindowPickerService windows, ILogger logger)
    {
        _capture = capture;
        _ocr = ocr;
        _translation = translation;
        _overlay = overlay;
        _profiles = profiles;
        _settings = settings;
        _engine = engine;
        _windows = windows;
        _logger = logger;
    }

    /// <summary>Show a translated snapshot, or take the shown one away.</summary>
    /// <param name="picked">The window picked on the main tab, if any.</param>
    public async Task ToggleAsync(WindowInfo? picked)
    {
        // Stop or a pause elsewhere may have hidden it already: then this press
        // means "translate", not "hide".
        if (_shown && _overlay.IsVisible)
        {
            Hide();
            return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var generation = Interlocked.Increment(ref _generation);
            var target = _engine.IsRunning
                ? new WindowInfo { Handle = _engine.TargetWindowHandle, Title = _engine.TargetWindowTitle }
                : picked ?? ActiveWindow();
            if (target == null || target.Handle == IntPtr.Zero)
            {
                _engine.ReportStatus(LanguageManager.Get("Strings.Snapshot.NoWindow"));
                return;
            }

            if (_engine.IsRunning && !_engine.IsUserPaused)
            {
                _engine.TogglePause();
                _pausedEngine = true;
            }
            _shown = true;
            _engine.ReportStatus(LanguageManager.Format("Strings.Snapshot.Working", target.Title));

            await Task.Run(() => TranslateAsync(target, generation));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Snapshot translation failed");
            _engine.ReportStatus(LanguageManager.Format("Strings.Snapshot.Failed", ex.Message));
            Hide();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Hide()
    {
        _shown = false;
        Interlocked.Increment(ref _generation);
        _overlay.Hide();
        if (_pausedEngine)
        {
            _pausedEngine = false;
            if (_engine.IsRunning && _engine.IsUserPaused) _engine.TogglePause();
        }
    }

    private async Task TranslateAsync(WindowInfo target, int generation)
    {
        bool StillWanted() => _shown && generation == Volatile.Read(ref _generation);

        if (!_engine.IsRunning) await PrepareForWindowAsync(target);
        if (!StillWanted()) return;

        using var frame = _capture.CaptureOnce(target.Handle);
        if (frame == null)
        {
            _engine.ReportStatus(LanguageManager.Get("Strings.Snapshot.NoPicture"));
            Hide();
            return;
        }

        byte[] png;
        using (var ms = new MemoryStream())
        {
            frame.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }
        var raw = _ocr.ProcessFrame(png);
        _engine.RecordSnapshotFrame(raw, frame.Width, frame.Height, target.Title);
        OcrTextHelpers.DropNoise(raw);
        OcrTextHelpers.DropOversized(raw);
        if (raw.Count == 0)
        {
            _engine.ReportStatus(LanguageManager.Get("Strings.Snapshot.NoText"));
            Hide();
            return;
        }

        // One frame, no history: plain grouping, no hysteresis or reuse.
        var regions = RegionGrouper.Group(raw);
        ColorSampler.Sample(frame, regions, raw);

        if (!WindowGeometry.TryGetClientRect(target.Handle, out var rect)) return;
        _overlay.TargetWindow = target.Handle;
        _overlay.HiddenByUser = false;

        var partial = new List<TranslationRegion>();
        void OnRegionUpdated(TranslationRegion r)
        {
            if (string.IsNullOrEmpty(r.TranslatedText)) return;
            List<TranslationRegion> copy;
            lock (partial)
            {
                if (!partial.Contains(r)) partial.Add(r);
                copy = new List<TranslationRegion>(partial);
            }
            _overlay.ShowRegions(copy, rect, isPartial: true, StillWanted);
        }

        var translated = await _translation.TranslateRegionsAsync(
            regions, _settings.Config.TargetLanguage, OnRegionUpdated, CancellationToken.None);
        if (!StillWanted()) return;
        _overlay.ShowRegions(translated, rect, isPartial: false, StillWanted);
        _engine.ReportStatus(LanguageManager.Format("Strings.Snapshot.Done", target.Title));
    }

    /// <summary>
    /// Without Start nothing is loaded for this game yet: pick its profile
    /// (OCR language, translator), reload what changed, wait for the OCR.
    /// </summary>
    private async Task PrepareForWindowAsync(WindowInfo target)
    {
        var profile = _profiles.GetOrCreateForWindow(target.Title, target.ProcessName);
        long? activeId = null;
        try { activeId = _profiles.ActiveProfile.Id; } catch (InvalidOperationException) { }
        if (activeId != profile.Id)
        {
            _profiles.ApplyProfile(profile);
            _ocr.Reload();
            _translation.Reload();
        }
        if (!_translation.IsReady) _translation.Initialize();

        // PaddleOCR takes a few seconds to load a model.
        for (int i = 0; i < 300 && _ocr.State != OcrBackendState.Ready; i++)
            await Task.Delay(100);
    }

    private WindowInfo? ActiveWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return null; // our own window has nothing to translate
        return _windows.GetVisibleWindows(includeFiltered: true).FirstOrDefault(w => w.Handle == hwnd)
               ?? new WindowInfo { Handle = hwnd, Title = string.Empty };
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
