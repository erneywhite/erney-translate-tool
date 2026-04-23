using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly AppSettings _appSettings;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;

    private string _apiKey = string.Empty;
    private string _testApiStatus = string.Empty;
    private bool _isTestingApi;
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

        TargetLanguages = new ObservableCollection<LanguageInfo>(LanguageInfo.GetSupportedTargetLanguages());
        SystemFonts = new ObservableCollection<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s));

        TestApiCommand = new RelayCommand(async _ => await TestApiAsync(), _ => !string.IsNullOrWhiteSpace(_apiKey) && !_isTestingApi);
        SaveCommand = new RelayCommand(_ => Save());

        LoadFromConfig();
        RefreshOcrLanguages();
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string TestApiStatus
    {
        get => _testApiStatus;
        set => SetProperty(ref _testApiStatus, value);
    }

    public bool IsTestingApi
    {
        get => _isTestingApi;
        set => SetProperty(ref _isTestingApi, value);
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

    public ICommand TestApiCommand { get; }
    public ICommand SaveCommand { get; }

    private void LoadFromConfig()
    {
        var c = _appSettings.Config;
        ApiKey = _appSettings.GetApiKey() ?? string.Empty;
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

    private async Task TestApiAsync()
    {
        try
        {
            IsTestingApi = true;
            TestApiStatus = "Проверка...";

            if (!string.IsNullOrWhiteSpace(ApiKey))
                _translationService.UpdateApiKey(ApiKey);

            var (ok, msg) = await _translationService.VerifyApiKeyAsync();
            TestApiStatus = msg;
            if (ok)
            {
                _appSettings.SetApiKey(ApiKey);
            }
        }
        catch (Exception ex)
        {
            TestApiStatus = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsTestingApi = false;
        }
    }

    private void Save()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
                _appSettings.SetApiKey(ApiKey);

            var c = _appSettings.Config;
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
            _appSettings.Save();

            MessageBox.Show("Настройки сохранены.", "Сохранено", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
