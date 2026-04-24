using System;
using System.Windows;
using System.Windows.Threading;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace ErneyTranslateTool.Core.Tray;

/// <summary>
/// Owns the system-tray icon: keeps its tooltip in sync with the engine
/// state, swaps the icon to reflect translation on/off, and exposes a
/// right-click menu so the user can toggle translation, hide overlay,
/// open settings or quit without ever bringing the main window forward.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Window _mainWindow;
    private readonly MainViewModel _mainVm;
    private readonly TranslationEngine _engine;
    private readonly CaptureService _capture;
    private readonly AppSettings _settings;
    private readonly ProfileManager _profiles;
    private readonly ILogger _logger;
    private bool _disposed;
    // Anything sticky we showed while the user wasn't looking — Attention
    // (e.g. "update available") survives engine state changes so it doesn't
    // get clobbered by Idle ↔ Translating churn. Cleared once the user
    // opens the main window.
    private TrayIconState _stickyState = TrayIconState.Idle;
    // Drives the Paused-state pulse: alternates icon between "with gray
    // dot" and "no dot" every ~700 ms so the user can tell paused apart
    // from idle (which is a steady gray dot).
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkOn;

    /// <summary>Raised when the user opens the main window via tray click/menu — owners use this to flush any pending modals deferred during a tray-only start.</summary>
    public event EventHandler? MainWindowOpened;
    public event EventHandler? ExitRequested;

    public TrayIconManager(Window mainWindow, MainViewModel mainVm,
        TranslationEngine engine, CaptureService capture,
        AppSettings settings, ProfileManager profiles, ILogger logger)
    {
        _mainWindow = mainWindow;
        _mainVm = mainVm;
        _engine = engine;
        _capture = capture;
        _settings = settings;
        _profiles = profiles;
        _logger = logger;

        _icon = new TaskbarIcon
        {
            // Idle = clean app icon, no badge. Renderer falls back to null
            // if loading fails — Hardcodet will then just show no icon
            // until RefreshIconAndTooltip retries on the first state change.
            IconSource = TrayIconRenderer.GetIconFor(TrayIconState.Idle),
            ToolTipText = "Erney's Translate Tool",
            Visibility = Visibility.Visible,
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _icon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _icon.ContextMenu = BuildMenu();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            ApplyIcon(ComputeState());
        };

        // Keep tooltip + icon up to date as engine state and stats change.
        // PauseStateChanged is the new signal — needs its own subscription
        // because StateChanged only fires for Start/Stop, not for the
        // capture loop's pause/resume transitions.
        _engine.StateChanged += (_, _) => RefreshIconAndTooltip();
        _engine.StatusUpdated += (_, _) => RefreshIconAndTooltip();
        _capture.PauseStateChanged += (_, _) => RefreshIconAndTooltip();
        _profiles.ActiveProfileChanged += (_, _) => RefreshIconAndTooltip();
        RefreshIconAndTooltip();
    }

    /// <summary>
    /// Compute the current effective tray-icon state. "Sticky" attention/error
    /// wins over engine state because the user hasn't acknowledged them yet.
    /// Otherwise the precedence is: paused (running but window iconic) →
    /// translating → idle.
    /// </summary>
    private TrayIconState ComputeState()
    {
        if (_stickyState == TrayIconState.Attention || _stickyState == TrayIconState.Error)
            return _stickyState;
        if (_engine.IsRunning && _capture.IsPaused) return TrayIconState.Paused;
        return _engine.IsRunning ? TrayIconState.Translating : TrayIconState.Idle;
    }

    /// <summary>
    /// Pin a sticky state (e.g. "update available" → Attention). It survives
    /// engine on/off until <see cref="ClearStickyState"/> is called or the
    /// user opens the main window.
    /// </summary>
    public void SetStickyState(TrayIconState state)
    {
        _stickyState = state;
        RefreshIconAndTooltip();
    }

    public void ClearStickyState()
    {
        _stickyState = TrayIconState.Idle;
        RefreshIconAndTooltip();
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var open = new System.Windows.Controls.MenuItem { Header = LanguageManager.Get("Strings.Tray.Open") };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var toggleTranslate = new System.Windows.Controls.MenuItem { Header = LanguageManager.Get("Strings.Tray.Toggle") };
        toggleTranslate.Click += (_, _) => _mainVm.ToggleFromHotkeyAsync().FireAndForgetSafeAsync();
        menu.Items.Add(toggleTranslate);

        var toggleOverlay = new System.Windows.Controls.MenuItem { Header = LanguageManager.Get("Strings.Tray.ToggleOverlay") };
        toggleOverlay.Click += (_, _) => _mainVm.ToggleOverlayFromHotkey();
        menu.Items.Add(toggleOverlay);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exit = new System.Windows.Controls.MenuItem { Header = LanguageManager.Get("Strings.Tray.Exit") };
        exit.Click += (_, _) =>
        {
            _logger.Information("Exit requested via tray menu");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(exit);

        return menu;
    }

    private void RefreshIconAndTooltip()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var state = ComputeState();
            // Don't bracket the active-profile line with "Default" — most
            // users never create profiles and the line would just be noise.
            var profileLine = !_profiles.ActiveProfile.IsDefault
                ? LanguageManager.Format("Strings.Tray.ProfileLine", _profiles.ActiveProfile.Name)
                : string.Empty;
            var stats = LanguageManager.Format("Strings.Tray.StatsLine",
                _settings.Config.CharactersTranslatedToday, _settings.GetCacheHitRate());

            // Highlight sticky attention/error in the tooltip so a glance
            // tells the user *why* the icon is yellow/red.
            var headline = state switch
            {
                TrayIconState.Attention   => LanguageManager.Get("Strings.Tray.HeadlineAttention"),
                TrayIconState.Error       => LanguageManager.Get("Strings.Tray.HeadlineError"),
                TrayIconState.Paused      => LanguageManager.Format("Strings.Tray.HeadlinePaused", _engine.TargetWindowTitle),
                TrayIconState.Translating => LanguageManager.Format("Strings.Tray.HeadlineActive", _engine.TargetWindowTitle),
                _                         => LanguageManager.Get("Strings.Tray.HeadlineIdle"),
            };
            _icon.ToolTipText = headline + profileLine + stats;

            // Start/stop the pulse alongside the paused state — no point
            // burning a timer tick while the user can see a steady dot.
            if (state == TrayIconState.Paused)
            {
                if (!_blinkTimer.IsEnabled)
                {
                    _blinkOn = true; // start the cycle on the visible half
                    _blinkTimer.Start();
                }
            }
            else
            {
                if (_blinkTimer.IsEnabled) _blinkTimer.Stop();
                _blinkOn = false;
            }

            ApplyIcon(state);
        });
    }

    /// <summary>
    /// Push the right pre-rendered icon into <see cref="_icon"/>. For Paused
    /// we alternate between the dotted variant and the bare app icon based
    /// on <see cref="_blinkOn"/> so the dot pulses.
    /// </summary>
    private void ApplyIcon(TrayIconState state)
    {
        System.Windows.Media.ImageSource? rendered;
        if (state == TrayIconState.Paused && !_blinkOn)
            rendered = TrayIconRenderer.GetBlankIcon();
        else
            rendered = TrayIconRenderer.GetIconFor(state);
        if (rendered != null) _icon.IconSource = rendered;
    }

    public void ShowBalloon(string title, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            _icon.ShowBalloonTip(title, message, BalloonIcon.Info));
    }

    private void ShowMainWindow()
    {
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;

        // Acknowledge any pending notification — once the user has the
        // window open they can see whatever it was for themselves.
        if (_stickyState != TrayIconState.Idle)
            ClearStickyState();

        MainWindowOpened?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _blinkTimer.Stop();
        _icon.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
