using System;
using System.Windows;
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
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private bool _disposed;
    // Anything sticky we showed while the user wasn't looking — Attention
    // (e.g. "update available") survives engine state changes so it doesn't
    // get clobbered by Idle ↔ Translating churn. Cleared once the user
    // opens the main window.
    private TrayIconState _stickyState = TrayIconState.Idle;

    /// <summary>Raised when the user opens the main window via tray click/menu — owners use this to flush any pending modals deferred during a tray-only start.</summary>
    public event EventHandler? MainWindowOpened;
    public event EventHandler? ExitRequested;

    public TrayIconManager(Window mainWindow, MainViewModel mainVm,
        TranslationEngine engine, AppSettings settings, ILogger logger)
    {
        _mainWindow = mainWindow;
        _mainVm = mainVm;
        _engine = engine;
        _settings = settings;
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

        // Keep tooltip + icon up to date as engine state and stats change.
        _engine.StateChanged += (_, _) => RefreshIconAndTooltip();
        _engine.StatusUpdated += (_, _) => RefreshIconAndTooltip();
        RefreshIconAndTooltip();
    }

    /// <summary>
    /// Compute the current effective tray-icon state. "Sticky" attention/error
    /// wins over the engine's idle/translating because the user explicitly
    /// hasn't acknowledged it yet.
    /// </summary>
    private TrayIconState ComputeState()
    {
        if (_stickyState == TrayIconState.Attention || _stickyState == TrayIconState.Error)
            return _stickyState;
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

        var open = new System.Windows.Controls.MenuItem { Header = "Открыть" };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var toggleTranslate = new System.Windows.Controls.MenuItem { Header = "Запустить / остановить перевод" };
        toggleTranslate.Click += (_, _) => _mainVm.ToggleFromHotkeyAsync().FireAndForgetSafeAsync();
        menu.Items.Add(toggleTranslate);

        var toggleOverlay = new System.Windows.Controls.MenuItem { Header = "Показать / скрыть оверлей" };
        toggleOverlay.Click += (_, _) => _mainVm.ToggleOverlayFromHotkey();
        menu.Items.Add(toggleOverlay);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exit = new System.Windows.Controls.MenuItem { Header = "Выход" };
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
            var running = _engine.IsRunning;
            var stats = $"\nПереведено сегодня: {_settings.Config.CharactersTranslatedToday:N0} симв." +
                        $"\nПопадания в кэш: {_settings.GetCacheHitRate():F1}%";

            // Highlight sticky attention/error in the tooltip so a glance
            // tells the user *why* the icon is yellow/red.
            var headline = _stickyState switch
            {
                TrayIconState.Attention => "Erney's Translate Tool — есть уведомление (открой окно)",
                TrayIconState.Error     => "Erney's Translate Tool — ошибка (открой окно)",
                _ => running
                    ? $"Erney's Translate Tool — перевод активен\n{_engine.TargetWindowTitle}"
                    : "Erney's Translate Tool — ожидание",
            };
            _icon.ToolTipText = headline + stats;

            // Swap the icon to the badged variant; falls back transparently
            // if the renderer can't produce one.
            var rendered = TrayIconRenderer.GetIconFor(ComputeState());
            if (rendered != null) _icon.IconSource = rendered;
        });
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
        _icon.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
