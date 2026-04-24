using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Tray;
using ErneyTranslateTool.Core.Updates;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.ViewModels;
using Serilog;

namespace ErneyTranslateTool;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkeys;
    private readonly AppSettings _settings;
    private readonly TranslationEngine _engine;
    private readonly UpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private TrayIconManager? _tray;
    private bool _allowRealClose;

    public MainViewModel MainVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public HistoryViewModel HistoryVM { get; }

    public MainWindow(
        MainViewModel mainVm,
        SettingsViewModel settingsVm,
        HistoryViewModel historyVm,
        HotkeyService hotkeys,
        AppSettings settings,
        TranslationEngine engine,
        UpdateChecker updateChecker,
        ILogger logger)
    {
        InitializeComponent();
        MainVM = mainVm;
        SettingsVM = settingsVm;
        HistoryVM = historyVm;
        _hotkeys = hotkeys;
        _settings = settings;
        _engine = engine;
        _updateChecker = updateChecker;
        _logger = logger;
        DataContext = this;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys.Initialize(this);
        RegisterHotkeys();

        _tray = new TrayIconManager(this, MainVM, _engine, _settings, _logger);
        _tray.ExitRequested += (_, _) => RealExit();

        // Background update check on startup. Failures are silent.
        if (_settings.Config.CheckForUpdatesOnStartup)
            _ = CheckForUpdatesAsync(showAlways: false);
    }

    private void RegisterHotkeys()
    {
        if (HotkeyParser.TryParse(_settings.Config.ToggleTranslationHotkey, out var mod1, out var vk1))
        {
            _hotkeys.RegisterHotkey("toggle-translation", mod1, vk1,
                () => MainVM.ToggleFromHotkeyAsync().FireAndForgetSafeAsync());
        }
        if (HotkeyParser.TryParse(_settings.Config.ToggleOverlayHotkey, out var mod2, out var vk2))
        {
            _hotkeys.RegisterHotkey("toggle-overlay", mod2, vk2,
                () => MainVM.ToggleOverlayFromHotkey());
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // [×] minimises to tray instead of exiting if the user has opted in
        // (default). They can still really exit via the tray menu.
        if (_settings.Config.CloseToTray && !_allowRealClose)
        {
            e.Cancel = true;
            Hide();

            // Only show the explanation balloon the first time — after that
            // the user knows the pattern and the popup just becomes noise.
            if (!_settings.Config.CloseToTrayBalloonShown)
            {
                _tray?.ShowBalloon("Erney's Translate Tool",
                    "Программа свёрнута в трей — кликни иконку чтобы открыть, или ПКМ → «Выход».");
                _settings.Config.CloseToTrayBalloonShown = true;
                _settings.Save();
            }
        }
    }

    private void RealExit()
    {
        _allowRealClose = true;
        Application.Current.Shutdown();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotkeys.UnregisterAll();
        _tray?.Dispose();
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool showAlways)
    {
        var info = await _updateChecker.CheckAsync();
        if (info == null)
        {
            if (showAlways)
                MessageBox.Show("Не удалось проверить обновления — нет интернета или GitHub недоступен.",
                    "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (info.IsNewer)
        {
            var msg = $"Доступна новая версия {info.Latest} (у тебя {info.Current}).\n\nОткрыть страницу релиза в браузере?";
            _tray?.ShowBalloon("Доступно обновление",
                $"Версия {info.Latest} вышла. Открой «О программе» чтобы скачать.");
            var result = MessageBox.Show(msg, "Обновление",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
                OpenInBrowser(info.ReleaseUrl);
        }
        else if (showAlways)
        {
            MessageBox.Show($"У тебя установлена последняя версия ({info.Current}).",
                "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Manual "Check for updates" entry point used by the About tab.</summary>
    public void RunManualUpdateCheck() => _ = CheckForUpdatesAsync(showAlways: true);

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* swallow */ }
    }
}
