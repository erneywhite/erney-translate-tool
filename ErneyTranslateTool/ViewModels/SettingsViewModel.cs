using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly AppSettings _appSettings;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;

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
    private string _installedOcrLanguages = string.Empty;

    public ObservableCollection<ProviderOption> Providers { get; }
    public ObservableCollection<LanguageInfo> TargetLanguages { get; }
    public ObservableCollection<string> SystemFonts { get; }
    public ObservableCollection<string> FontSizeModes { get; } = new() { "Auto", "Manual" };

    public SettingsViewModel(
        AppSettings appSettings,
        TranslationService translationService,
        OcrService ocrService)
    {
        _appSettings = appSettings;
        _translationService = translationService;
        _ocrService = ocrService;

        Providers = new ObservableCollection<ProviderOption>(
            TranslatorFactory.AllProviders.Select(p =>
                new ProviderOption(p, TranslatorFactory.DisplayName(p))));

        TargetLanguages = new ObservableCollection<LanguageInfo>(LanguageInfo.GetSupportedTargetLanguages());
        SystemFonts = new ObservableCollection<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s));

        TestProviderCommand = new RelayCommand(async _ => await TestProviderAsync(), _ => !_isTesting);
        SaveCommand = new RelayCommand(_ => Save());

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
            "DeepL: лучшее качество. Зарегистрируйся на deepl.com/pro-api (Sign up for free). " +
            "Бесплатный тариф 500 000 симв./мес, но требует привязку карты. Ключ заканчивается на «:fx».",
        TranslatorFactory.ProviderMyMemory =>
            "MyMemory: бесплатно, без карты. 5 000 символов в день анонимно, " +
            "50 000 — если указать любой свой email (он отправляется как параметр запроса).",
        TranslatorFactory.ProviderGoogleFree =>
            "Google Translate (бесплатный публичный endpoint): без регистрации, без ключа, без карты. " +
            "Неофициально — Google теоретически может ограничить или изменить, но обычно работает стабильно.",
        TranslatorFactory.ProviderLibreTranslate =>
            "LibreTranslate: open-source. Можно использовать публичный инстанс или развернуть свой. " +
            "Часть инстансов требует API-ключ.",
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

    public string InstalledOcrLanguages
    {
        get => _installedOcrLanguages;
        set => SetProperty(ref _installedOcrLanguages, value);
    }

    public ICommand TestProviderCommand { get; }
    public ICommand SaveCommand { get; }

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
    }

    private void RefreshOcrLanguages()
    {
        var langs = _ocrService.GetAvailableLanguages();
        InstalledOcrLanguages = langs.Count == 0
            ? "Не установлено ни одного пакета"
            : string.Join(", ", langs.Select(l => $"{l.DisplayName} ({l.Tag})"));
    }

    private async Task TestProviderAsync()
    {
        try
        {
            IsTesting = true;
            TestStatus = "Проверка...";

            // Persist creds first so the factory can pick them up.
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

            MessageBox.Show("Настройки сохранены.", "Сохранено",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public record ProviderOption(string Id, string DisplayName);
