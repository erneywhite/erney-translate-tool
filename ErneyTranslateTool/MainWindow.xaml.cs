using System;
using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.ViewModels;

namespace ErneyTranslateTool;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkeys;
    private readonly AppSettings _settings;

    public MainViewModel MainVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public HistoryViewModel HistoryVM { get; }

    public MainWindow(
        MainViewModel mainVm,
        SettingsViewModel settingsVm,
        HistoryViewModel historyVm,
        HotkeyService hotkeys,
        AppSettings settings)
    {
        InitializeComponent();
        MainVM = mainVm;
        SettingsVM = settingsVm;
        HistoryVM = historyVm;
        _hotkeys = hotkeys;
        _settings = settings;
        DataContext = this;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys.Initialize(this);
        RegisterHotkeys();
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotkeys.UnregisterAll();
    }
}
