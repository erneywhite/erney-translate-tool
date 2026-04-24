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
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly AppSettings _appSettings;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;
    private readonly TessdataManager _tessdata;

    private string _selectedProvider = TranslatorFactory.ProviderMyMemory;
    private string _deeplApiKey = string.Empty;
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
    private string _fontSizeMode = "Auto";
    private string _toggleTranslationHotkey = "Ctrl+Shift+T";
    private string _toggleOverlayHotkey = "Ctrl+Shift+H";
    private string _selectedOcrEngine = OcrService.EnginePaddle;
    private OcrLanguageOption? _selectedOcrLanguage;
    private string _saveStatus = string.Empty;
    private int _saveStatusToken;
    private bool _useBestTessdata = true;

    public ObservableCollection<ProviderOption> Providers { get; }
    public ObservableCollection<LanguageInfo> TargetLanguages { get; }
    public ObservableCollection<string> SystemFonts { get; }
    public ObservableCollection<string> FontSizeModes { get; } = new() { "Auto", "Manual" };
    public ObservableCollection<EngineOption> OcrEngines { get; }
    public ObservableCollection<OcrLanguageOption> OcrLanguages { get; } = new();
    public ObservableCollection<TessdataItem> TessdataCatalog { get; } = new();

    public SettingsViewModel(
        AppSettings appSettings,
        TranslationService translationService,
        OcrService ocrService,
        TessdataManager tessdata)
    {
        _appSettings = appSettings;
        _translationService = translationService;
        _ocrService = ocrService;
        _tessdata = tessdata;

        Providers = new ObservableCollection<ProviderOption>(
            TranslatorFactory.AllProviders.Select(p =>
                new ProviderOption(p, TranslatorFactory.DisplayName(p))));

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

        BuildTessdataCatalog();
        LoadFromConfig();
        RefreshOcrLanguages();
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
                OnPropertyChanged(nameof(ProviderHelpText));
            }
        }
    }

    public bool IsDeepL => _selectedProvider == TranslatorFactory.ProviderDeepL;
    public bool IsMyMemory => _selectedProvider == TranslatorFactory.ProviderMyMemory;
    public bool IsGoogleFree => _selectedProvider == TranslatorFactory.ProviderGoogleFree;
    public bool IsLibre => _selectedProvider == TranslatorFactory.ProviderLibreTranslate;

    public string ProviderHelpText => _selectedProvider switch
    {
        TranslatorFactory.ProviderDeepL =>
            "DeepL: лучшее качество. Регистрация на deepl.com/pro-api. " +
            "Бесплатный тариф 500 000 симв./мес, но требует привязку карты. Ключ заканчивается на «:fx».",
        TranslatorFactory.ProviderMyMemory =>
            "MyMemory: бесплатно, без карты. 5 000 символов в день анонимно, " +
            "50 000 — если указать любой свой email.",
        TranslatorFactory.ProviderGoogleFree =>
            "Google Translate (бесплатный публичный endpoint): без регистрации, без ключа, без карты.",
        TranslatorFactory.ProviderLibreTranslate =>
            "LibreTranslate: open-source. Можно использовать публичный инстанс или свой собственный.",
        _ => string.Empty
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

    private void LoadFromConfig()
    {
        var c = _appSettings.Config;
        SelectedProvider = string.IsNullOrWhiteSpace(c.TranslationProvider)
            ? TranslatorFactory.ProviderMyMemory
            : c.TranslationProvider;
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
        FontSizeMode = c.FontSizeMode;
        ToggleTranslationHotkey = c.ToggleTranslationHotkey;
        ToggleOverlayHotkey = c.ToggleOverlayHotkey;

        SelectedOcrEngine = string.IsNullOrWhiteSpace(c.OcrEngine) ? OcrService.EnginePaddle : c.OcrEngine;
        UseBestTessdata = c.UseBestTessdata;
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
            if (IsDeepL && !string.IsNullOrWhiteSpace(DeepLApiKey))
                _appSettings.SetApiKey(DeepLApiKey);
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
        c.MyMemoryEmail = MyMemoryEmail ?? string.Empty;
        c.LibreTranslateUrl = string.IsNullOrWhiteSpace(LibreUrl) ? "https://libretranslate.com" : LibreUrl;
        c.LibreTranslateApiKey = LibreApiKey ?? string.Empty;
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
        c.FontSizeMode = FontSizeMode;
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
            _appSettings.Save();
            _translationService.Reload();
            _ocrService.Reload();

            ShowTransientStatus("Сохранено ✓");
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
