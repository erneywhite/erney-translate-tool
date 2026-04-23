using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Data;

namespace ErneyTranslateTool.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly TranslationEngine _engine;
    private readonly WindowPickerService _windowPicker;
    private readonly AppSettings _settings;

    private string _statusMessage = "Ожидание";
    private string _selectedWindowTitle = "(окно не выбрано)";
    private WindowInfo? _selectedWindow;
    private bool _isRunning;
    private int _charactersTranslatedToday;
    private double _cacheHitRate;

    public ObservableCollection<WindowInfo> Windows { get; } = new();

    public MainViewModel(
        TranslationEngine engine,
        WindowPickerService windowPicker,
        AppSettings settings)
    {
        _engine = engine;
        _windowPicker = windowPicker;
        _settings = settings;

        _engine.StateChanged += (_, _) =>
        {
            IsRunning = _engine.IsRunning;
            OnPropertyChanged(nameof(ToggleButtonText));
        };
        _engine.StatusUpdated += (_, msg) => StatusMessage = msg;

        RefreshWindowsCommand = new RelayCommand(_ => RefreshWindows(), _ => !IsRunning);
        ToggleEngineCommand = new RelayCommand(async _ => await ToggleEngineAsync(), _ => CanToggle);
        RefreshStatsCommand = new RelayCommand(_ => RefreshStats());

        RefreshStats();
        RefreshWindows();
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SelectedWindowTitle
    {
        get => _selectedWindowTitle;
        set => SetProperty(ref _selectedWindowTitle, value);
    }

    public WindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                SelectedWindowTitle = value?.Title ?? "(окно не выбрано)";
                OnPropertyChanged(nameof(CanToggle));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ToggleButtonText));
                OnPropertyChanged(nameof(CanToggle));
            }
        }
    }

    public int CharactersTranslatedToday
    {
        get => _charactersTranslatedToday;
        set => SetProperty(ref _charactersTranslatedToday, value);
    }

    public double CacheHitRate
    {
        get => _cacheHitRate;
        set => SetProperty(ref _cacheHitRate, value);
    }

    public string ToggleButtonText => IsRunning ? "Остановить" : "Запустить";
    public bool CanToggle => SelectedWindow != null;

    public ICommand RefreshWindowsCommand { get; }
    public ICommand ToggleEngineCommand { get; }
    public ICommand RefreshStatsCommand { get; }

    private void RefreshWindows()
    {
        Windows.Clear();
        foreach (var w in _windowPicker.GetVisibleWindows())
            Windows.Add(w);
    }

    public void RefreshStats()
    {
        CharactersTranslatedToday = _settings.Config.CharactersTranslatedToday;
        CacheHitRate = _settings.GetCacheHitRate();
    }

    private async Task ToggleEngineAsync()
    {
        try
        {
            if (_engine.IsRunning)
            {
                await _engine.StopAsync();
            }
            else if (SelectedWindow != null)
            {
                await _engine.StartAsync(SelectedWindow.Handle, SelectedWindow.Title);
            }
            RefreshStats();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
            MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ToggleFromHotkeyAsync()
    {
        await ToggleEngineAsync();
    }

    public void ToggleOverlayFromHotkey()
    {
        _engine.ToggleOverlay();
    }
}
