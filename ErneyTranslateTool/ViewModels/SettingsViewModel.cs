using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Core.Startup;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly AppSettings _appSettings;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;
    private readonly TessdataManager _tessdata;
    private readonly CacheRepository _cache;
    private readonly ProfileManager _profiles;
    private readonly ILogger _logger;

    private string _selectedProvider = TranslatorFactory.ProviderMyMemory;
    private string _selectedFallbackProvider = string.Empty;
    private string _deeplApiKey = string.Empty;
    private string _openAIApiKey = string.Empty;
    private string _openAIModel = "gpt-4o-mini";
    private string _anthropicApiKey = string.Empty;
    private string _anthropicModel = "claude-haiku-4-5";
    private double _llmTemperature = 0.3;
    private bool _llmUseContext = true;
    private int _llmContextSize = 3;
    private string _myMemoryEmail = string.Empty;
    private string _libreUrl = "https://libretranslate.com";
    private string _libreApiKey = string.Empty;
    private string _testStatus = string.Empty;
    private bool _isTesting;
    private LanguageInfo? _selectedTargetLanguage;
    private string _overlayFontFamily = "Segoe UI";
    private double _overlayOpacity;
    private string _backgroundColor = "#1A1A1A";
    private string _textColor = "#FFFFFF";
    private double _manualFontSize;
    private double _overlayCornerRadius = 4;
    private string _fontSizeMode = "Auto";
    private string _toggleTranslationHotkey = "Ctrl+Shift+T";
    private string _toggleOverlayHotkey = "Ctrl+Shift+H";
    private string _selectedOcrEngine = OcrService.EnginePaddle;
    private OcrLanguageOption? _selectedOcrLanguage;
    private string _saveStatus = string.Empty;
    private int _saveStatusToken;
    private bool _useBestTessdata = true;
    private string _ocrStatus = string.Empty;
    private Brush _ocrStatusColor = MakeBrush("#9CA3AF");
    private string _selectedAppTheme = "Dark";
    private bool _closeToTray = true;
    private bool _checkForUpdatesOnStartup = true;
    private bool _autoStartWithWindows;

    private static Brush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public ObservableCollection<ProviderOption> Providers { get; }
    /// <summary>Same list as <see cref="Providers"/> plus a "no fallback" sentinel at the top. Filled in the constructor.</summary>
    public ObservableCollection<ProviderOption> FallbackProviders { get; }
    public ObservableCollection<LanguageInfo> TargetLanguages { get; }
    public ObservableCollection<string> SystemFonts { get; }
    public ObservableCollection<string> FontSizeModes { get; } = new() { "Auto", "Manual" };
    public ObservableCollection<EngineOption> OcrEngines { get; }
    public ObservableCollection<OcrLanguageOption> OcrLanguages { get; } = new();
    public ObservableCollection<TessdataItem> TessdataCatalog { get; } = new();

    /// <summary>App-wide UI themes (whole window colour palette).</summary>
    public ObservableCollection<EngineOption> AppThemes { get; } = new(
        ThemeManager.Available.Select(t => new EngineOption(t.Id, t.DisplayName)));

    public string SelectedAppTheme
    {
        get => _selectedAppTheme;
        set
        {
            if (SetProperty(ref _selectedAppTheme, value))
            {
                // Live-apply the moment the user picks a theme — Save just
                // persists the choice. Better UX than waiting for Save.
                ThemeManager.Apply(value);
            }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
            {
                // Take effect immediately so the user can test "x" closes to
                // tray without restarting.
                _appSettings.Config.CloseToTray = value;
            }
        }
    }

    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetProperty(ref _checkForUpdatesOnStartup, value);
    }

    /// <summary>
    /// Mirrors the per-user HKCU\...\Run registry key. Backed by the registry
    /// rather than AppConfig — the OS owns the truth for "autostart yes/no",
    /// and other tools (msconfig, Autoruns) can flip it independently.
    /// </summary>
    public bool AutoStartWithWindows
    {
        get => _autoStartWithWindows;
        set
        {
            if (SetProperty(ref _autoStartWithWindows, value))
            {
                // Apply immediately so the user can verify with Task Manager
                // or msconfig without clicking "Save first".
                if (value) AutoStartManager.Enable(_logger);
                else AutoStartManager.Disable(_logger);
            }
        }
    }


    /// <summary>Pre-baked overlay colour combinations users can pick with one click.</summary>
    public ObservableCollection<OverlayPreset> OverlayPresets { get; } = new()
    {
        new("Классика",          "#000000", "#FFFFFF", 0.95, 4),
        new("Тёмная мягкая",     "#1F2937", "#F3F4F6", 0.92, 6),
        new("Светлая",           "#FFFFFF", "#000000", 0.95, 4),
        new("Sepia",             "#3A2814", "#F5DEB3", 0.92, 6),
        new("Cyber neon",        "#0A1929", "#00E5FF", 0.95, 2),
        new("Discord",           "#36393F", "#DCDDDE", 0.95, 6),
        new("Hi-contrast yellow","#000000", "#FFEB3B", 1.00, 0),
        new("Glass",             "#1A1A1A", "#FFFFFF", 0.65, 8),
    };

    public SettingsViewModel(
        AppSettings appSettings,
        TranslationService translationService,
        OcrService ocrService,
        TessdataManager tessdata,
        CacheRepository cache,
        ProfileManager profiles,
        ILogger logger)
    {
        _appSettings = appSettings;
        _translationService = translationService;
        _ocrService = ocrService;
        _tessdata = tessdata;
        _cache = cache;
        _profiles = profiles;
        _logger = logger;

        Providers = new ObservableCollection<ProviderOption>(
            TranslatorFactory.AllProviders.Select(p =>
                new ProviderOption(p, TranslatorFactory.DisplayName(p))));

        FallbackProviders = new ObservableCollection<ProviderOption>();
        FallbackProviders.Add(new ProviderOption(string.Empty, "Без резервного"));
        foreach (var p in TranslatorFactory.AllProviders)
            FallbackProviders.Add(new ProviderOption(p, TranslatorFactory.DisplayName(p)));

        TargetLanguages = new ObservableCollection<LanguageInfo>(LanguageInfo.GetSupportedTargetLanguages());
        SystemFonts = new ObservableCollection<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s));

        OcrEngines = new ObservableCollection<EngineOption>
        {
            new(OcrService.EnginePaddle, "PaddleOCR (рекомендуется — лучшая точность, нейросеть; модель качается с интернета на первом запуске)"),
            new(OcrService.EngineTesseract, "Tesseract (быстрее, но хуже распознаёт стилизованные шрифты)"),
            new(OcrService.EngineWindows, "Windows OCR (нужны системные языковые пакеты)")
        };

        TestProviderCommand = new RelayCommand(async _ => await TestProviderAsync(), _ => !_isTesting);
        SaveCommand = new RelayCommand(_ => Save());
        OpenWindowsLanguageSettingsCommand = new RelayCommand(_ => OpenWindowsLanguageSettings());
        DownloadLanguageCommand = new RelayCommand(async p => await DownloadLanguageAsync(p as TessdataItem));
        DeleteLanguageCommand = new RelayCommand(p => DeleteLanguage(p as TessdataItem));
        RefreshOcrLanguagesCommand = new RelayCommand(_ => RefreshOcrLanguages());
        ApplyOverlayPresetCommand = new RelayCommand(p => ApplyOverlayPreset(p as OverlayPreset));
        ClearCacheCommand = new RelayCommand(_ => ClearCache());
        RefreshCacheStatsCommand = new RelayCommand(_ => RefreshCacheStats());

        BuildTessdataCatalog();
        LoadFromConfig();
        RefreshOcrLanguages();
        RefreshCacheStats();

        // Live status indicator for the active OCR backend (download / init /
        // ready state) — important for PaddleOCR which can be loading models
        // for minutes the first time you pick a new language.
        _ocrService.StatusChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(UpdateOcrStatus);
        UpdateOcrStatus();
    }

    public string OcrStatus
    {
        get => _ocrStatus;
        private set => SetProperty(ref _ocrStatus, value);
    }

    public Brush OcrStatusColor
    {
        get => _ocrStatusColor;
        private set => SetProperty(ref _ocrStatusColor, value);
    }

    private void UpdateOcrStatus()
    {
        OcrStatus = $"{_ocrService.CurrentEngine}: {_ocrService.StatusMessage}";
        OcrStatusColor = _ocrService.State switch
        {
            OcrBackendState.Ready => MakeBrush("#10B981"),   // green
            OcrBackendState.Loading => MakeBrush("#F59E0B"), // amber
            OcrBackendState.Failed => MakeBrush("#EF4444"),  // red
            _ => MakeBrush("#9CA3AF")                        // gray
        };
    }

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(IsDeepL));
                OnPropertyChanged(nameof(IsMyMemory));
                OnPropertyChanged(nameof(IsGoogleFree));
                OnPropertyChanged(nameof(IsLibre));
                OnPropertyChanged(nameof(IsOpenAI));
                OnPropertyChanged(nameof(IsAnthropic));
                OnPropertyChanged(nameof(IsLlm));
                OnPropertyChanged(nameof(ProviderHelpText));
            }
        }
    }

    /// <summary>
    /// Bound to the "Резервный провайдер" dropdown. Empty string means
    /// "no fallback" (single-translator mode). Saved to
    /// <see cref="AppConfig.FallbackProvider"/> on Save.
    /// </summary>
    public string SelectedFallbackProvider
    {
        get => _selectedFallbackProvider;
        set => SetProperty(ref _selectedFallbackProvider, value);
    }

    public bool IsDeepL => _selectedProvider == TranslatorFactory.ProviderDeepL;
    public bool IsMyMemory => _selectedProvider == TranslatorFactory.ProviderMyMemory;
    public bool IsGoogleFree => _selectedProvider == TranslatorFactory.ProviderGoogleFree;
    public bool IsLibre => _selectedProvider == TranslatorFactory.ProviderLibreTranslate;
    public bool IsOpenAI => _selectedProvider == TranslatorFactory.ProviderOpenAI;
    public bool IsAnthropic => _selectedProvider == TranslatorFactory.ProviderAnthropic;
    /// <summary>True for any LLM-backed provider — drives the visibility of the shared "LLM settings" group (temperature, context).</summary>
    public bool IsLlm => IsOpenAI || IsAnthropic;

    public string ProviderHelpText => _selectedProvider switch
    {
        TranslatorFactory.ProviderDeepL =>
            "DeepL: лучшее качество среди классических переводчиков. Регистрация на deepl.com/pro-api. " +
            "Бесплатный тариф 500 000 симв./мес, но требует привязку карты. Ключ заканчивается на «:fx».",
        TranslatorFactory.ProviderMyMemory =>
            "MyMemory: бесплатно, без карты. 5 000 символов в день анонимно, " +
            "50 000 — если указать любой свой email.",
        TranslatorFactory.ProviderGoogleFree =>
            "Google Translate (бесплатный публичный endpoint): без регистрации, без ключа, без карты.",
        TranslatorFactory.ProviderLibreTranslate =>
            "LibreTranslate: open-source. Можно использовать публичный инстанс или свой собственный.",
        TranslatorFactory.ProviderOpenAI =>
            "OpenAI: качественный нейросетевой перевод (особенно для художественных диалогов и игр). " +
            "Нужен платный API-ключ с platform.openai.com. Стоимость ~$0.01–0.05 за час игры с диалогами " +
            "на gpt-4o-mini, дороже на gpt-4o. Поддерживается контекст последних реплик.",
        TranslatorFactory.ProviderAnthropic =>
            "Anthropic Claude: качественный LLM-перевод. Нужен платный API-ключ с console.anthropic.com. " +
            "Стоимость близка к OpenAI — claude-haiku-4-5 дешёвый и быстрый, claude-sonnet-4-5 точнее. " +
            "Поддерживается контекст последних реплик.",
        _ => string.Empty
    };

    public string OpenAIApiKey
    {
        get => _openAIApiKey;
        set => SetProperty(ref _openAIApiKey, value);
    }

    public string OpenAIModel
    {
        get => _openAIModel;
        set => SetProperty(ref _openAIModel, value);
    }

    public string AnthropicApiKey
    {
        get => _anthropicApiKey;
        set => SetProperty(ref _anthropicApiKey, value);
    }

    public string AnthropicModel
    {
        get => _anthropicModel;
        set => SetProperty(ref _anthropicModel, value);
    }

    public double LlmTemperature
    {
        get => _llmTemperature;
        set => SetProperty(ref _llmTemperature, value);
    }

    public bool LlmUseContext
    {
        get => _llmUseContext;
        set => SetProperty(ref _llmUseContext, value);
    }

    public int LlmContextSize
    {
        get => _llmContextSize;
        set => SetProperty(ref _llmContextSize, value);
    }

    /// <summary>Suggested OpenAI models — user can also type their own in the textbox.</summary>
    public ObservableCollection<string> OpenAIModelPresets { get; } = new()
    {
        "gpt-4o-mini",
        "gpt-4o",
        "gpt-4-turbo",
        "gpt-3.5-turbo",
    };

    /// <summary>Suggested Anthropic models — user can also type their own.</summary>
    public ObservableCollection<string> AnthropicModelPresets { get; } = new()
    {
        "claude-haiku-4-5",
        "claude-sonnet-4-5",
        "claude-3-5-haiku-latest",
        "claude-3-5-sonnet-latest",
    };

    public string DeepLApiKey
    {
        get => _deeplApiKey;
        set => SetProperty(ref _deeplApiKey, value);
    }

    public string MyMemoryEmail
    {
        get => _myMemoryEmail;
        set => SetProperty(ref _myMemoryEmail, value);
    }

    public string LibreUrl
    {
        get => _libreUrl;
        set => SetProperty(ref _libreUrl, value);
    }

    public string LibreApiKey
    {
        get => _libreApiKey;
        set => SetProperty(ref _libreApiKey, value);
    }

    public string TestStatus
    {
        get => _testStatus;
        set => SetProperty(ref _testStatus, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
    }

    public LanguageInfo? SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set => SetProperty(ref _selectedTargetLanguage, value);
    }

    public string OverlayFontFamily
    {
        get => _overlayFontFamily;
        set => SetProperty(ref _overlayFontFamily, value);
    }

    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => SetProperty(ref _overlayOpacity, value);
    }

    public string BackgroundColor
    {
        get => _backgroundColor;
        set => SetProperty(ref _backgroundColor, value);
    }

    public string TextColor
    {
        get => _textColor;
        set => SetProperty(ref _textColor, value);
    }

    public double ManualFontSize
    {
        get => _manualFontSize;
        set => SetProperty(ref _manualFontSize, value);
    }

    public double OverlayCornerRadius
    {
        get => _overlayCornerRadius;
        set => SetProperty(ref _overlayCornerRadius, value);
    }

    public string FontSizeMode
    {
        get => _fontSizeMode;
        set => SetProperty(ref _fontSizeMode, value);
    }

    public string ToggleTranslationHotkey
    {
        get => _toggleTranslationHotkey;
        set => SetProperty(ref _toggleTranslationHotkey, value);
    }

    public string ToggleOverlayHotkey
    {
        get => _toggleOverlayHotkey;
        set => SetProperty(ref _toggleOverlayHotkey, value);
    }

    public string SelectedOcrEngine
    {
        get => _selectedOcrEngine;
        set
        {
            if (SetProperty(ref _selectedOcrEngine, value))
            {
                OnPropertyChanged(nameof(IsWindowsOcr));
                OnPropertyChanged(nameof(IsTesseract));
                OnPropertyChanged(nameof(IsPaddle));
                RefreshOcrLanguages();
            }
        }
    }

    public bool IsWindowsOcr => _selectedOcrEngine == OcrService.EngineWindows;
    public bool IsTesseract => _selectedOcrEngine == OcrService.EngineTesseract;
    public bool IsPaddle => _selectedOcrEngine == OcrService.EnginePaddle;

    public string SaveStatus
    {
        get => _saveStatus;
        set => SetProperty(ref _saveStatus, value);
    }

    /// <summary>
    /// When true, downloads pull from tessdata_best (4-5x larger but markedly
    /// better quality on stylized / pixel-art / small fonts).
    /// </summary>
    public bool UseBestTessdata
    {
        get => _useBestTessdata;
        set => SetProperty(ref _useBestTessdata, value);
    }

    public OcrLanguageOption? SelectedOcrLanguage
    {
        get => _selectedOcrLanguage;
        set => SetProperty(ref _selectedOcrLanguage, value);
    }

    public ICommand TestProviderCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenWindowsLanguageSettingsCommand { get; }
    public ICommand DownloadLanguageCommand { get; }
    public ICommand DeleteLanguageCommand { get; }
    public ICommand RefreshOcrLanguagesCommand { get; }
    public ICommand ApplyOverlayPresetCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand RefreshCacheStatsCommand { get; }

    /// <summary>Cache size limit options shown in the dropdown. 0 == "no limit".</summary>
    public ObservableCollection<CacheSizeOption> CacheSizeOptions { get; } = new()
    {
        new(50,   "50 МБ"),
        new(200,  "200 МБ (по умолчанию)"),
        new(500,  "500 МБ"),
        new(1000, "1 ГБ"),
        new(0,    "Без лимита"),
    };

    private CacheSizeOption? _selectedCacheSizeOption;
    public CacheSizeOption? SelectedCacheSizeOption
    {
        get => _selectedCacheSizeOption;
        set
        {
            if (SetProperty(ref _selectedCacheSizeOption, value) && value != null)
            {
                // Take effect immediately; the next translation will trigger
                // a background eviction if we just dropped below current size.
                _appSettings.Config.MaxCacheSizeMb = value.Mb;
                _appSettings.Save();
                RefreshCacheStats();
            }
        }
    }

    private string _cacheStatsText = "—";
    /// <summary>"1 234 записи · 18 МБ из 200 МБ" or similar.</summary>
    public string CacheStatsText
    {
        get => _cacheStatsText;
        private set => SetProperty(ref _cacheStatsText, value);
    }

    public void RefreshCacheStats()
    {
        try
        {
            var (entries, bytes) = _cache.GetStats();
            var mb = bytes / 1024.0 / 1024.0;
            var limit = _appSettings.Config.MaxCacheSizeMb;
            CacheStatsText = limit > 0
                ? $"{entries:N0} записей · {mb:F1} МБ из {limit} МБ"
                : $"{entries:N0} записей · {mb:F1} МБ (без лимита)";
        }
        catch
        {
            CacheStatsText = "Не удалось прочитать статистику кэша";
        }
    }

    private void ClearCache()
    {
        if (MessageBox.Show(
                "Полностью очистить кэш переводов? Все сохранённые переводы будут удалены, " +
                "повторные переводы пойдут заново через провайдер.",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        var deleted = _cache.ClearCache();
        RefreshCacheStats();
        MessageBox.Show($"Удалено записей: {deleted:N0}.", "Кэш очищен",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyOverlayPreset(OverlayPreset? preset)
    {
        if (preset == null) return;
        BackgroundColor = preset.Background;
        TextColor = preset.Text;
        OverlayOpacity = preset.Opacity;
        OverlayCornerRadius = preset.CornerRadius;
    }

    private void LoadFromConfig()
    {
        var c = _appSettings.Config;
        SelectedProvider = string.IsNullOrWhiteSpace(c.TranslationProvider)
            ? TranslatorFactory.ProviderMyMemory
            : c.TranslationProvider;
        SelectedFallbackProvider = c.FallbackProvider ?? string.Empty;

        OpenAIApiKey = _appSettings.GetOpenAIKey() ?? string.Empty;
        OpenAIModel = string.IsNullOrWhiteSpace(c.OpenAIModel) ? "gpt-4o-mini" : c.OpenAIModel;
        AnthropicApiKey = _appSettings.GetAnthropicKey() ?? string.Empty;
        AnthropicModel = string.IsNullOrWhiteSpace(c.AnthropicModel) ? "claude-haiku-4-5" : c.AnthropicModel;
        LlmTemperature = c.LlmTemperature;
        LlmUseContext = c.LlmUseContext;
        LlmContextSize = c.LlmContextSize;
        DeepLApiKey = _appSettings.GetApiKey() ?? string.Empty;
        MyMemoryEmail = c.MyMemoryEmail;
        LibreUrl = string.IsNullOrWhiteSpace(c.LibreTranslateUrl) ? "https://libretranslate.com" : c.LibreTranslateUrl;
        LibreApiKey = c.LibreTranslateApiKey;

        SelectedTargetLanguage = TargetLanguages.FirstOrDefault(l =>
            string.Equals(l.Code, c.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            ?? TargetLanguages.FirstOrDefault();
        OverlayFontFamily = c.OverlayFontFamily;
        OverlayOpacity = c.OverlayOpacity;
        BackgroundColor = c.BackgroundColor;
        TextColor = c.TextColor;
        ManualFontSize = c.ManualFontSize;
        OverlayCornerRadius = c.OverlayCornerRadius;
        FontSizeMode = c.FontSizeMode;
        ToggleTranslationHotkey = c.ToggleTranslationHotkey;
        ToggleOverlayHotkey = c.ToggleOverlayHotkey;

        SelectedOcrEngine = string.IsNullOrWhiteSpace(c.OcrEngine) ? OcrService.EnginePaddle : c.OcrEngine;
        UseBestTessdata = c.UseBestTessdata;
        SelectedAppTheme = string.IsNullOrWhiteSpace(c.AppTheme) ? ThemeManager.Dark : c.AppTheme;
        CloseToTray = c.CloseToTray;
        CheckForUpdatesOnStartup = c.CheckForUpdatesOnStartup;
        // Source-of-truth for autostart is the registry, not the config —
        // setting field directly bypasses the property setter so we don't
        // re-write to the registry while just reading state.
        _autoStartWithWindows = AutoStartManager.IsEnabled(_logger);
        OnPropertyChanged(nameof(AutoStartWithWindows));

        // Match the saved limit to one of our preset options; fall back to
        // 200 MB if the stored value isn't in the dropdown (manual edit).
        SelectedCacheSizeOption = CacheSizeOptions.FirstOrDefault(o => o.Mb == c.MaxCacheSizeMb)
            ?? CacheSizeOptions.First(o => o.Mb == 200);
    }

    public void RefreshOcrLanguages()
    {
        OcrLanguages.Clear();
        if (IsTesseract)
        {
            foreach (var code in _tessdata.GetInstalledLanguageCodes())
                OcrLanguages.Add(new OcrLanguageOption(code, $"{TesseractLanguages.DisplayNameFor(code)} ({code})"));

            var saved = _appSettings.Config.TesseractLanguage;
            SelectedOcrLanguage = OcrLanguages.FirstOrDefault(o =>
                string.Equals(o.Tag, saved, StringComparison.OrdinalIgnoreCase))
                ?? OcrLanguages.FirstOrDefault(o => o.Tag == "eng")
                ?? OcrLanguages.FirstOrDefault();
        }
        else if (IsPaddle)
        {
            // Single source of truth: PaddleOcrBackend owns the catalog so
            // we don't have to keep this list in sync by hand.
            foreach (var (tag, display) in PaddleOcrBackend.SupportedLanguages)
                OcrLanguages.Add(new OcrLanguageOption(tag, display));

            var saved = _appSettings.Config.PaddleLanguage;
            SelectedOcrLanguage = OcrLanguages.FirstOrDefault(o =>
                string.Equals(o.Tag, saved, StringComparison.OrdinalIgnoreCase))
                ?? OcrLanguages.First();
        }
        else
        {
            // Windows OCR
            try
            {
                foreach (var (tag, display) in
                    Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)))
                {
                    OcrLanguages.Add(new OcrLanguageOption(tag, $"{display} ({tag})"));
                }
            }
            catch { /* ignore */ }

            var saved = _appSettings.Config.SourceLanguage;
            SelectedOcrLanguage = OcrLanguages.FirstOrDefault(o =>
                string.Equals(o.Tag, saved, StringComparison.OrdinalIgnoreCase))
                ?? OcrLanguages.FirstOrDefault();
        }
    }

    private void BuildTessdataCatalog()
    {
        TessdataCatalog.Clear();
        foreach (var entry in TesseractLanguages.Catalog)
        {
            TessdataCatalog.Add(new TessdataItem
            {
                Code = entry.Code,
                DisplayName = entry.DisplayName,
                SizeText = $"~{entry.SizeMb:F1} МБ",
                IsInstalled = _tessdata.IsInstalled(entry.Code)
            });
        }
    }

    private async Task DownloadLanguageAsync(TessdataItem? item)
    {
        if (item == null || item.IsBusy || item.IsInstalled) return;
        try
        {
            item.IsBusy = true;
            item.StatusText = UseBestTessdata ? "Скачивание (best)..." : "Скачивание...";
            await _tessdata.DownloadLanguageAsync(item.Code, UseBestTessdata);
            item.IsInstalled = true;
            item.StatusText = string.Empty;
            RefreshOcrLanguages();
        }
        catch (Exception ex)
        {
            item.StatusText = $"Ошибка: {ex.Message}";
            MessageBox.Show($"Не удалось скачать {item.Code}: {ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void DeleteLanguage(TessdataItem? item)
    {
        if (item == null || !item.IsInstalled) return;
        if (MessageBox.Show($"Удалить пакет {item.DisplayName} ({item.Code})?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (_tessdata.DeleteLanguage(item.Code))
        {
            item.IsInstalled = false;
            RefreshOcrLanguages();
        }
    }

    private void OpenWindowsLanguageSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть параметры Windows: {ex.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task TestProviderAsync()
    {
        try
        {
            IsTesting = true;
            TestStatus = "Проверка...";

            ApplyToConfig();
            // Persist keys before Verify so the just-edited values are
            // picked up by the freshly-built translator.
            if (IsDeepL && !string.IsNullOrWhiteSpace(DeepLApiKey))
                _appSettings.SetApiKey(DeepLApiKey);
            if (IsOpenAI) _appSettings.SetOpenAIKey(OpenAIApiKey ?? string.Empty);
            if (IsAnthropic) _appSettings.SetAnthropicKey(AnthropicApiKey ?? string.Empty);
            _appSettings.Save();

            _translationService.Reload();
            var (ok, msg) = await _translationService.VerifyAsync();
            TestStatus = msg;
        }
        catch (Exception ex)
        {
            TestStatus = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ApplyToConfig()
    {
        var c = _appSettings.Config;
        c.TranslationProvider = SelectedProvider;
        // Don't save a fallback identical to primary — that would be a no-op
        // at runtime and just confuse the next read.
        c.FallbackProvider = string.Equals(SelectedFallbackProvider, SelectedProvider, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : (SelectedFallbackProvider ?? string.Empty);
        c.MyMemoryEmail = MyMemoryEmail ?? string.Empty;
        c.LibreTranslateUrl = string.IsNullOrWhiteSpace(LibreUrl) ? "https://libretranslate.com" : LibreUrl;
        c.LibreTranslateApiKey = LibreApiKey ?? string.Empty;
        c.OpenAIModel = string.IsNullOrWhiteSpace(OpenAIModel) ? "gpt-4o-mini" : OpenAIModel;
        c.AnthropicModel = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-haiku-4-5" : AnthropicModel;
        c.LlmTemperature = LlmTemperature;
        c.LlmUseContext = LlmUseContext;
        c.LlmContextSize = LlmContextSize;
        if (SelectedTargetLanguage != null)
            c.TargetLanguage = SelectedTargetLanguage.Code;
        c.OcrEngine = SelectedOcrEngine;
        if (SelectedOcrLanguage != null)
        {
            if (IsTesseract) c.TesseractLanguage = SelectedOcrLanguage.Tag;
            else if (IsPaddle) c.PaddleLanguage = SelectedOcrLanguage.Tag;
            else c.SourceLanguage = SelectedOcrLanguage.Tag;
        }
        c.UseBestTessdata = UseBestTessdata;
        c.OverlayFontFamily = OverlayFontFamily;
        c.OverlayOpacity = OverlayOpacity;
        c.BackgroundColor = BackgroundColor;
        c.TextColor = TextColor;
        c.ManualFontSize = ManualFontSize;
        c.OverlayCornerRadius = OverlayCornerRadius;
        c.FontSizeMode = FontSizeMode;
        c.AppTheme = SelectedAppTheme;
        c.CloseToTray = CloseToTray;
        c.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        c.ToggleTranslationHotkey = ToggleTranslationHotkey;
        c.ToggleOverlayHotkey = ToggleOverlayHotkey;
    }

    private void Save()
    {
        try
        {
            ApplyToConfig();
            if (!string.IsNullOrWhiteSpace(DeepLApiKey))
                _appSettings.SetApiKey(DeepLApiKey);
            // Always pass through to the setter — empty string clears the
            // stored key, which is how the user "removes" it.
            _appSettings.SetOpenAIKey(OpenAIApiKey ?? string.Empty);
            _appSettings.SetAnthropicKey(AnthropicApiKey ?? string.Empty);
            _appSettings.Save();
            _translationService.Reload();
            _ocrService.Reload();

            // Mirror the profile-affecting subset of AppConfig back into
            // the active profile so the user's tweaks "stick" to the game
            // they were playing — not just the global config.
            _profiles.SaveActiveProfileFromCurrentConfig();

            var profileName = _profiles.ActiveProfile.IsDefault
                ? "По умолчанию"
                : _profiles.ActiveProfile.Name;
            ShowTransientStatus($"Сохранено ✓ (профиль: {profileName})");
        }
        catch (Exception ex)
        {
            ShowTransientStatus("Ошибка: " + ex.Message);
        }
    }

    private async void ShowTransientStatus(string message)
    {
        SaveStatus = message;
        var token = ++_saveStatusToken;
        await Task.Delay(3000);
        if (_saveStatusToken == token)
            SaveStatus = string.Empty;
    }
}

public record ProviderOption(string Id, string DisplayName);
public record OcrLanguageOption(string Tag, string DisplayName);
public record EngineOption(string Id, string DisplayName);
public record OverlayPreset(string Name, string Background, string Text, double Opacity, double CornerRadius);
public record CacheSizeOption(int Mb, string DisplayName);

public class TessdataItem : BaseViewModel
{
    private bool _isInstalled;
    private bool _isBusy;
    private string _statusText = string.Empty;

    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
                OnPropertyChanged(nameof(CanDownload));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanDownload));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool CanDownload => !IsInstalled && !IsBusy;
}
