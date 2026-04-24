using System;
using System.ComponentModel;
using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Core.Tray;
using ErneyTranslateTool.Core.Updates;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.ViewModels;
using ErneyTranslateTool.Views.Dialogs;
using Serilog;

namespace ErneyTranslateTool;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkeys;
    private readonly AppSettings _settings;
    private readonly TranslationEngine _engine;
    private readonly CaptureService _capture;
    private readonly ProfileManager _profiles;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateDownloader _updateDownloader;
    private readonly ILogger _logger;
    private TrayIconManager? _tray;
    private bool _allowRealClose;
    // When started minimised we don't pop modals over a hidden window —
    // we stash them here and flush when the user opens the main window.
    private UpdateCheckResult? _pendingUpdate;
    private Action? _pendingWhatsNew;

    public MainViewModel MainVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public HistoryViewModel HistoryVM { get; }
    public GlossaryViewModel GlossaryVM { get; }
    public ProfilesViewModel ProfilesVM { get; }

    public MainWindow(
        MainViewModel mainVm,
        SettingsViewModel settingsVm,
        HistoryViewModel historyVm,
        GlossaryViewModel glossaryVm,
        ProfilesViewModel profilesVm,
        HotkeyService hotkeys,
        AppSettings settings,
        TranslationEngine engine,
        CaptureService capture,
        ProfileManager profiles,
        UpdateChecker updateChecker,
        UpdateDownloader updateDownloader,
        ILogger logger)
    {
        InitializeComponent();
        MainVM = mainVm;
        SettingsVM = settingsVm;
        HistoryVM = historyVm;
        GlossaryVM = glossaryVm;
        ProfilesVM = profilesVm;
        _hotkeys = hotkeys;
        _settings = settings;
        _engine = engine;
        _capture = capture;
        _profiles = profiles;
        _updateChecker = updateChecker;
        _updateDownloader = updateDownloader;
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

        _tray = new TrayIconManager(this, MainVM, _engine, _capture, _settings, _profiles, _logger);
        _tray.ExitRequested += (_, _) => RealExit();
        _tray.MainWindowOpened += (_, _) => FlushPendingDialogs();

        // Sidebar starts on Main — same as the old TabControl default.
        // Selecting an item raises SelectionChanged which flips the
        // visible content panel.
        if (NavList.Items.Count > 0) NavList.SelectedIndex = 0;

        // Background update check on startup. Failures are silent.
        if (_settings.Config.CheckForUpdatesOnStartup)
            _ = CheckForUpdatesAsync(showAlways: false);
    }

    /// <summary>
    /// Sidebar nav handler. Maps the selected item's <c>Tag</c> to one
    /// of the pre-instantiated tab UserControls and flips its
    /// Visibility, leaving all the others Collapsed. Keeping every tab
    /// alive (rather than reconstructing) preserves their internal
    /// state across navigation, same as the old TabControl behaviour.
    /// </summary>
    private void OnNavSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not System.Windows.Controls.ListBoxItem item) return;
        var tag = item.Tag as string ?? string.Empty;

        MainTabHost.Visibility        = tag == "Main"        ? Visibility.Visible : Visibility.Collapsed;
        TranslationTabHost.Visibility = tag == "Translation" ? Visibility.Visible : Visibility.Collapsed;
        OverlayTabHost.Visibility     = tag == "Overlay"     ? Visibility.Visible : Visibility.Collapsed;
        HistoryTabHost.Visibility     = tag == "History"     ? Visibility.Visible : Visibility.Collapsed;
        GlossaryTabHost.Visibility    = tag == "Glossary"    ? Visibility.Visible : Visibility.Collapsed;
        ProfilesTabHost.Visibility    = tag == "Profiles"    ? Visibility.Visible : Visibility.Collapsed;
        AppSettingsTabHost.Visibility = tag == "AppSettings" ? Visibility.Visible : Visibility.Collapsed;
        AboutTabHost.Visibility       = tag == "About"       ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// When the app started in tray-only mode and discovered an update or
    /// just-upgraded notes, those were stashed instead of shown over a
    /// hidden window. Flush them now that the user has opened the window.
    /// </summary>
    private void FlushPendingDialogs()
    {
        if (_pendingUpdate is { } up)
        {
            _pendingUpdate = null;
            ShowUpdateDialog(up);
        }
        if (_pendingWhatsNew is { } wn)
        {
            _pendingWhatsNew = null;
            // Run on dispatcher so it lands after the current ShowMainWindow
            // call finishes activating us.
            Dispatcher.BeginInvoke(wn);
        }
    }

    /// <summary>Called by App at startup to schedule the "What's new" dialog for the first time the window opens (tray-only start case).</summary>
    public void DeferWhatsNewDialog(Action showDialog)
    {
        _pendingWhatsNew = showDialog;
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
                    LanguageManager.Get("Strings.Tray.MinimisedBalloon"));
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
        var result = await _updateChecker.CheckAsync();

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpdateAvailable:
                // Two paths: visible window → modal dialog as before.
                // Tray-only start → balloon + amber dot + stash, so we
                // don't pop a modal over a hidden owner. The user will
                // see it the moment they click the tray icon.
                if (!IsVisible)
                {
                    _pendingUpdate = result;
                    _tray?.SetStickyState(TrayIconState.Attention);
                    _tray?.ShowBalloon(LanguageManager.Get("Strings.Tray.UpdateBalloonTitle"),
                        LanguageManager.Format("Strings.Tray.UpdateDeferredBody", result.Latest!));
                }
                else
                {
                    _tray?.ShowBalloon(LanguageManager.Get("Strings.Tray.UpdateBalloonTitle"),
                        LanguageManager.Format("Strings.Tray.UpdateBalloonBody", result.Latest!));
                    ShowUpdateDialog(result);
                }
                break;

            case UpdateCheckOutcome.UpToDate:
                if (showAlways)
                    MessageBox.Show(
                        LanguageManager.Format("Strings.UpdateCheck.UpToDateFmt", result.Current),
                        LanguageManager.Get("Strings.UpdateCheck.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateCheckOutcome.NoReleases:
                if (showAlways)
                    MessageBox.Show(
                        LanguageManager.Format("Strings.UpdateCheck.NoReleasesFmt", result.Current),
                        LanguageManager.Get("Strings.UpdateCheck.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateCheckOutcome.Error:
                if (showAlways)
                    MessageBox.Show(
                        LanguageManager.Format("Strings.UpdateCheck.ErrorFmt", result.ErrorMessage),
                        LanguageManager.Get("Strings.UpdateCheck.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    /// <summary>
    /// Show the in-app update dialog. If the user clicks "Update now" and the
    /// installer launches successfully, we shut the app down so the installer
    /// can replace files; the new exe is auto-launched by Inno Setup's [Run]
    /// section.
    /// </summary>
    private void ShowUpdateDialog(UpdateCheckResult result)
    {
        // Make sure the main window is visible — if the user has it minimised
        // to tray, the modal would otherwise be invisible behind it.
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        var dlg = new UpdateAvailableDialog(result, _updateDownloader, _logger) { Owner = this };
        dlg.ShowDialog();

        if (dlg.ShouldExitForUpdate)
        {
            _logger.Information("Update installer launched, shutting down to allow file replacement");
            RealExit();
        }
    }

    /// <summary>Manual "Check for updates" entry point used by the About tab.</summary>
    public void RunManualUpdateCheck() => _ = CheckForUpdatesAsync(showAlways: true);
}
