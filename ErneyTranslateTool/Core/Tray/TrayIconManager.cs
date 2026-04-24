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
            IconSource = LoadIcon(),
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

    private System.Windows.Media.ImageSource? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/Icons/app.ico", UriKind.Absolute);
            return System.Windows.Media.Imaging.BitmapFrame.Create(uri);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load tray icon");
            return null;
        }
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
            _icon.ToolTipText = running
                ? $"Erney's Translate Tool — перевод активен\n{_engine.TargetWindowTitle}{stats}"
                : $"Erney's Translate Tool — ожидание{stats}";
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _icon.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
