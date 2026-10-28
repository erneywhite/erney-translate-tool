using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.ViewModels;

/// <summary>
/// v1.0.31 Main tab ("control panel"): status chips, the picked window with
/// a big Start, a searchable window list with real app icons, and a live
/// feed of translations. While the engine runs the picker is replaced by a
/// card for the running game with Pause / Hide overlay / Stop.
/// </summary>
public class MainViewModel : BaseViewModel
{
    private const int FeedCapacity = 30;
    private const int PreviewCount = 3;

    private readonly TranslationEngine _engine;
    private readonly WindowPickerService _windowPicker;
    private readonly AppSettings _settings;
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly ProfileManager _profiles;
    private readonly HistoryRepository _history;
    // Refreshes stats and status chips once a second. Chips read cheap
    // fields (OCR state, provider ids) — polling keeps them right after a
    // change on another tab without wiring an event for every setting.
    private readonly DispatcherTimer _refreshTimer;

    private string _statusMessage = LanguageManager.Get("Strings.Main.StatusWaiting");
    private WindowItem? _selectedWindow;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isOverlayHidden;
    private int _charactersTranslatedToday;
    private double _cacheHitRate;
    private string _frameTimeText = "—";
    private bool _showHiddenWindows;
    private string _searchText = string.Empty;
    private string _ocrChipText = string.Empty;
    private Brush _ocrDotBrush = Brushes.Gray;
    private string _providerChipText = string.Empty;
    private string _fallbackChipText = string.Empty;
    private string _selectedSubtitle = string.Empty;
    private string _runningSubtitle = string.Empty;

    public ObservableCollection<WindowItem> Windows { get; } = new();
    /// <summary>Filtered view of <see cref="Windows"/> by <see cref="SearchText"/>.</summary>
    public ICollectionView WindowsView { get; }
    /// <summary>Newest first; the running screen shows all, the idle card <see cref="RecentPreview"/>.</summary>
    public ObservableCollection<FeedItem> RecentTranslations { get; } = new();
    public ObservableCollection<FeedItem> RecentPreview { get; } = new();

    /// <summary>Raised by "All history" — MainWindow switches to the History tab.</summary>
    public event EventHandler? OpenHistoryRequested;

    public MainViewModel(
        TranslationEngine engine,
        WindowPickerService windowPicker,
        AppSettings settings,
        OcrService ocr,
        TranslationService translation,
        ProfileManager profiles,
        HistoryRepository history,
        SnapshotService snapshot)
    {
        _snapshot = snapshot;
        _engine = engine;
        _windowPicker = windowPicker;
        _settings = settings;
        _ocr = ocr;
        _translation = translation;
        _profiles = profiles;
        _history = history;

        WindowsView = CollectionViewSource.GetDefaultView(Windows);
        WindowsView.Filter = MatchesSearch;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => { RefreshStats(); RefreshStatus(); };
        _refreshTimer.Start();

        // Engine events can come from the capture loop (pause on minimise),
        // so everything touching bound state goes through the dispatcher.
        _engine.StateChanged += (_, _) => OnUi(SyncEngineState);
        _engine.UserPauseChanged += (_, _) => OnUi(SyncEngineState);
        _engine.StatusUpdated += (_, msg) => OnUi(() => StatusMessage = msg);
        _history.TranslationAdded += (_, entry) => OnUi(() => AddToFeed(entry));

        RefreshWindowsCommand = new RelayCommand(_ => RefreshWindows(), _ => !IsRunning);
        ToggleEngineCommand = new RelayCommand(async _ => await ToggleEngineAsync(), _ => CanToggle);
        TogglePauseCommand = new RelayCommand(_ => _engine.TogglePause(), _ => IsRunning);
        ToggleOverlayCommand = new RelayCommand(_ => _engine.ToggleOverlay(), _ => IsRunning);
        ToggleSnapshotCommand = new RelayCommand(_ => ToggleSnapshotAsync().FireAndForgetSafeAsync(), _ => IsRunning || SelectedWindow != null);
        OpenHistoryCommand = new RelayCommand(_ => OpenHistoryRequested?.Invoke(this, EventArgs.Empty));

        // Direct field assign so the setter's persist + refresh doesn't run on load.
        _showHiddenWindows = _settings.Config.ShowHiddenWindowsInPicker;

        // Over-fetch: unchanged "translations" are filtered out of the feed.
        foreach (var entry in _history.GetRecentTranslations(PreviewCount * 5))
        {
            if (RecentTranslations.Count >= PreviewCount) break;
            AddToFeed(entry, atEnd: true);
        }

        RefreshStats();
        RefreshStatus();
        RefreshWindows();
    }

    // ---- window picker ----

    /// <summary>Persisted "show system / background windows" toggle (v1.0.28).</summary>
    public bool ShowHiddenWindows
    {
        get => _showHiddenWindows;
        set
        {
            if (SetProperty(ref _showHiddenWindows, value))
            {
                _settings.Config.ShowHiddenWindowsInPicker = value;
                _settings.Save();
                RefreshWindows();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                WindowsView.Refresh();
        }
    }

    public WindowItem? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                OnPropertyChanged(nameof(SelectedWindowTitle));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanToggle));
                SelectedSubtitle = BuildSelectedSubtitle(value);
            }
        }
    }

    public bool HasSelection => SelectedWindow != null;
    public string SelectedWindowTitle => SelectedWindow?.Title ?? LanguageManager.Get("Strings.Main.NoWindow");

    /// <summary>"yankee_massage.exe · профиль «…»" under the picked window's name.</summary>
    public string SelectedSubtitle
    {
        get => _selectedSubtitle;
        private set => SetProperty(ref _selectedSubtitle, value);
    }

    private bool MatchesSearch(object obj)
    {
        if (string.IsNullOrWhiteSpace(_searchText) || obj is not WindowItem w) return true;
        var q = _searchText.Trim();
        return w.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || w.ProcessLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshWindows()
    {
        var previous = SelectedWindow?.Info.Handle;
        Windows.Clear();
        foreach (var w in _windowPicker.GetVisibleWindows(_showHiddenWindows))
            Windows.Add(new WindowItem(w));
        // Keep the selection across a refresh when the window still exists.
        SelectedWindow = previous == null ? null : Windows.FirstOrDefault(w => w.Info.Handle == previous);
    }

    private string BuildSelectedSubtitle(WindowItem? w)
    {
        if (w == null) return LanguageManager.Get("Strings.Main.PickHint");
        var profile = _profiles.FindForWindow(w.Title, w.Info.ProcessName);
        var profilePart = profile.IsDefault
            ? LanguageManager.Get("Strings.Main.ProfileNew")
            : LanguageManager.Format("Strings.Main.ProfileFmt", profile.Name);
        return $"{w.ProcessLabel} · {profilePart}";
    }

    // ---- engine state ----

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanToggle));
            }
        }
    }

    public bool IsIdle => !IsRunning;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(RunningLabel));
            }
        }
    }

    public bool IsOverlayHidden
    {
        get => _isOverlayHidden;
        private set
        {
            if (SetProperty(ref _isOverlayHidden, value))
                OnPropertyChanged(nameof(OverlayButtonText));
        }
    }

    public string PauseButtonText => LanguageManager.Get(IsPaused ? "Strings.Main.Resume" : "Strings.Main.Pause");
    public string OverlayButtonText => LanguageManager.Get(IsOverlayHidden ? "Strings.Main.ShowOverlay" : "Strings.Main.HideOverlay");
    public string RunningLabel => LanguageManager.Get(IsPaused ? "Strings.Main.PausedLabel" : "Strings.Main.RunningLabel");

    /// <summary>"Профиль «…» · Английский → Русский" on the running card.</summary>
    public string RunningSubtitle
    {
        get => _runningSubtitle;
        private set => SetProperty(ref _runningSubtitle, value);
    }

    public bool CanToggle => IsRunning || SelectedWindow != null;

    private void SyncEngineState()
    {
        IsRunning = _engine.IsRunning;
        IsPaused = _engine.IsUserPaused;
        IsOverlayHidden = _engine.IsOverlayHiddenByUser;
        if (IsRunning)
        {
            var target = LanguageInfo.GetSupportedTargetLanguages()
                .FirstOrDefault(l => l.Code == _settings.Config.TargetLanguage)?.DisplayName
                ?? _settings.Config.TargetLanguage;
            RunningSubtitle = LanguageManager.Format("Strings.Main.RunningSubtitleFmt",
                _profiles.ActiveProfile.Name, OcrLanguageName(), target);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- status chips ----

    public string OcrChipText { get => _ocrChipText; private set => SetProperty(ref _ocrChipText, value); }
    public Brush OcrDotBrush { get => _ocrDotBrush; private set => SetProperty(ref _ocrDotBrush, value); }
    public string ProviderChipText { get => _providerChipText; private set => SetProperty(ref _providerChipText, value); }
    public string FallbackChipText { get => _fallbackChipText; private set => SetProperty(ref _fallbackChipText, value); }

    private void RefreshStatus()
    {
        switch (_ocr.State)
        {
            case OcrBackendState.Ready:
                OcrChipText = LanguageManager.Format("Strings.Main.Chip.OcrReadyFmt", _ocr.CurrentEngine, OcrLanguageName());
                OcrDotBrush = ThemeBrush("SuccessBrush");
                break;
            case OcrBackendState.Loading:
                OcrChipText = LanguageManager.Get("Strings.Main.Chip.OcrLoading");
                OcrDotBrush = ThemeBrush("WarningBrush");
                break;
            case OcrBackendState.Failed:
                OcrChipText = LanguageManager.Get("Strings.Main.Chip.OcrFailed");
                OcrDotBrush = ThemeBrush("ErrorBrush");
                break;
            default:
                OcrChipText = LanguageManager.Get("Strings.Main.Chip.OcrOff");
                OcrDotBrush = ThemeBrush("TextDisabledBrush");
                break;
        }

        var cfg = _settings.Config;
        ProviderChipText = LanguageManager.Format("Strings.Main.Chip.ProviderFmt",
            Core.Translators.TranslatorFactory.ShortName(cfg.TranslationProvider));
        FallbackChipText = string.IsNullOrEmpty(cfg.FallbackProvider)
            ? LanguageManager.Get("Strings.Main.Chip.FallbackNone")
            : LanguageManager.Format(_translation.IsUsingFallback
                ? "Strings.Main.Chip.FallbackActiveFmt"
                : "Strings.Main.Chip.FallbackFmt", Core.Translators.TranslatorFactory.ShortName(cfg.FallbackProvider));
    }

    private string OcrLanguageName()
    {
        var tag = _ocr.CurrentLanguageTag;
        return _ocr.GetAvailableLanguages().FirstOrDefault(l => l.Tag == tag).DisplayName ?? tag;
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // ---- stats ----

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

    /// <summary>Average frame time in ms as a bare number, "—" before the first frame.</summary>
    public string FrameTimeText
    {
        get => _frameTimeText;
        set => SetProperty(ref _frameTimeText, value);
    }

    public string HotkeyHint => LanguageManager.Format("Strings.Main.HotkeyHintFmt",
        _settings.Config.ToggleTranslationHotkey, _settings.Config.ToggleOverlayHotkey);
    public string StartHotkey => _settings.Config.ToggleTranslationHotkey;
    public string SnapshotHotkey => _settings.Config.SnapshotHotkey;
    public bool HasSnapshotHotkey => !string.IsNullOrWhiteSpace(_settings.Config.SnapshotHotkey);

    public void RefreshStats()
    {
        CharactersTranslatedToday = _settings.Config.CharactersTranslatedToday;
        CacheHitRate = _settings.GetCacheHitRate();
        var avg = _engine.AverageFrameMs;
        FrameTimeText = avg <= 0 ? "—" : $"{avg:F0}";
        OnPropertyChanged(nameof(HotkeyHint));
        OnPropertyChanged(nameof(StartHotkey));
        OnPropertyChanged(nameof(SnapshotHotkey));
        OnPropertyChanged(nameof(HasSnapshotHotkey));
    }

    // ---- live feed ----

    public bool HasFeed => RecentTranslations.Count > 0;

    private void AddToFeed(TranslationEntry entry, bool atEnd = false)
    {
        // "100%" -> "100%", "-" -> "-": UI chrome and numbers the translator
        // returned unchanged. They're real history but say nothing in a feed.
        if (string.Equals(entry.OriginalText.Trim(), entry.TranslatedText.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        var item = new FeedItem(entry.Timestamp.ToLocalTime().ToString("HH:mm"),
            entry.OriginalText, entry.TranslatedText);
        if (atEnd)
        {
            RecentTranslations.Add(item);
        }
        else
        {
            // An exact glossary hit is recorded on every full frame — don't
            // let one line fill the whole feed.
            if (RecentTranslations.Count > 0 && RecentTranslations[0].Original == item.Original) return;
            RecentTranslations.Insert(0, item);
            while (RecentTranslations.Count > FeedCapacity) RecentTranslations.RemoveAt(RecentTranslations.Count - 1);
        }

        RecentPreview.Clear();
        foreach (var f in RecentTranslations.Take(PreviewCount)) RecentPreview.Add(f);
        OnPropertyChanged(nameof(HasFeed));
    }

    // ---- commands ----

    public ICommand RefreshWindowsCommand { get; }
    public ICommand ToggleEngineCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand ToggleOverlayCommand { get; }
    public ICommand ToggleSnapshotCommand { get; }
    public ICommand OpenHistoryCommand { get; }

    private async Task ToggleEngineAsync()
    {
        try
        {
            if (_engine.IsRunning)
                await _engine.StopAsync();
            else if (SelectedWindow != null)
                await _engine.StartAsync(SelectedWindow.Info.Handle, SelectedWindow.Title, SelectedWindow.Info.ProcessName);
            RefreshStats();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
            MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ToggleFromHotkeyAsync() => await ToggleEngineAsync();

    public void ToggleOverlayFromHotkey() => _engine.ToggleOverlay();

    /// <summary>
    /// Hotkey pause/resume: a soft flag inside the engine — capture, OCR
    /// backend, LLM context and stats stay alive, unlike a full stop.
    /// </summary>
    public void TogglePauseFromHotkey() => _engine.TogglePause();

    private readonly SnapshotService _snapshot;

    /// <summary>
    /// v1.0.37: translate one snapshot of the picked (or, from the hotkey, the
    /// active) window and freeze it; a second call removes it.
    /// </summary>
    public Task ToggleSnapshotAsync() => _snapshot.ToggleAsync(SelectedWindow?.Info);

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}

/// <summary>One line of the live translation feed on the Main tab.</summary>
public sealed record FeedItem(string Time, string Original, string Translated);
