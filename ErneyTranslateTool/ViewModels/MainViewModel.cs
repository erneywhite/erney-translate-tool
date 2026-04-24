using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Data;

namespace ErneyTranslateTool.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly TranslationEngine _engine;
    private readonly WindowPickerService _windowPicker;
    private readonly AppSettings _settings;
    // Polls engine + settings for live stats while translation is active.
    // Cheaper than wiring an event for every translated character — once a
    // second is plenty for human-readable counters.
    private readonly DispatcherTimer _statsTimer;

    // Localised lazily — LanguageManager.Get reads from the live merged
    // dictionaries, so the value follows whatever language is loaded
    // when the property is first accessed.
    private string _statusMessage = LanguageManager.Get("Strings.Main.StatusWaiting");
    private string _selectedWindowTitle = LanguageManager.Get("Strings.Main.NoWindow");
    private WindowInfo? _selectedWindow;
    private bool _isRunning;
    private int _charactersTranslatedToday;
    private double _cacheHitRate;
    private string _frameTimeText = "—";

    public ObservableCollection<WindowInfo> Windows { get; } = new();

    public MainViewModel(
        TranslationEngine engine,
        WindowPickerService windowPicker,
        AppSettings settings)
    {
        _engine = engine;
        _windowPicker = windowPicker;
        _settings = settings;

        // Construct the timer BEFORE wiring the engine event so the handler
        // can safely reference it without a null-warning.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();

        _engine.StateChanged += (_, _) =>
        {
            IsRunning = _engine.IsRunning;
            OnPropertyChanged(nameof(ToggleButtonText));
            // Start/stop the live-stats poll alongside the engine state.
            if (IsRunning) _statsTimer.Start();
            else _statsTimer.Stop();
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
                SelectedWindowTitle = value?.Title ?? LanguageManager.Get("Strings.Main.NoWindow");
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

    /// <summary>Human-readable per-frame latency, e.g. "340 мс (последний 280)" or "—".</summary>
    public string FrameTimeText
    {
        get => _frameTimeText;
        set => SetProperty(ref _frameTimeText, value);
    }

    public string ToggleButtonText => IsRunning
        ? LanguageManager.Get("Strings.Main.Stop")
        : LanguageManager.Get("Strings.Main.Start");
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

        var avg = _engine.AverageFrameMs;
        var last = _engine.LastFrameMs;
        FrameTimeText = avg <= 0
            ? "—"
            : $"{avg:F0} мс (последний {last} мс)";
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
                await _engine.StartAsync(SelectedWindow.Handle, SelectedWindow.Title, SelectedWindow.ProcessName);
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

    /// <summary>
    /// Hotkey entry point for pause/resume. Unlike ToggleFromHotkeyAsync
    /// which does a full engine start/stop, this flips a soft flag inside
    /// the engine — capture service, OCR backend, LLM conversation
    /// history, live stats counters all stay alive. Intended for brief
    /// interruptions (cutscene, incoming call) where you don't want to
    /// pay the Start cost all over again when you resume.
    /// </summary>
    public void TogglePauseFromHotkey()
    {
        _engine.TogglePause();
    }
}
