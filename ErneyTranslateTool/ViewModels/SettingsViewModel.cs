using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ErneyTranslateTool.Services;
using ErneyTranslateTool.Repositories;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.ViewModels
{
    public class SettingsViewModel : BaseViewModel
    {
        private readonly AppSettings _appSettings;
        private readonly IHotkeyService _hotkeyService;
        private readonly ITranslationService _translationService;

        // API Settings
        private string _googleApiKey;
        private string _deeplApiKey;
        private string _yandexApiKey;
        private string _selectedTranslationProvider;
        private ObservableCollection<string> _translationProviders;

        // Capture Settings
        private int _captureDelay;
        private bool _captureCursor;
        private string _captureFormat;

        // OCR Settings
        private string _selectedOcrEngine;
        private bool _ocrPreprocessImage;
        private int _ocrScaleFactor;

        // Overlay Settings
        private double _overlayOpacity;
        private int _overlayFontSize;
        private string _overlayPosition;
        private string _overlayFontFamily;
        private string _overlayBackgroundColor;
        private string _overlayTextColor;
        private bool _overlayAlwaysOnTop;
        private bool _overlayClickThrough;

        // Hotkey Settings
        private string _captureHotkey;
        private string _translateHotkey;
        private string _toggleOverlayHotkey;
        private string _hideOverlayHotkey;

        // General Settings
        private bool _autoStart;
        private bool _minimizeToTray;
        private bool _showNotifications;
        private string _selectedLanguage;
        private int _cacheSize;
        private int _historyLimit;
        private bool _autoTranslateAfterCapture;
        private bool _showTranslationInOverlay;

        public SettingsViewModel(
            AppSettings appSettings,
            IHotkeyService hotkeyService,
            ITranslationService translationService)
        {
            _appSettings = appSettings;
            _hotkeyService = hotkeyService;
            _translationService = translationService;

            _translationProviders = new ObservableCollection<string>
            {
                "Google Translate",
                "DeepL",
                "Yandex Translate"
            };

            LoadSettings();
            RegisterCommands();
        }

        // API Settings Properties
        public string GoogleApiKey
        {
            get => _googleApiKey;
            set => SetProperty(ref _googleApiKey, value);
        }

        public string DeeplApiKey
        {
            get => _deeplApiKey;
            set => SetProperty(ref _deeplApiKey, value);
        }

        public string YandexApiKey
        {
            get => _yandexApiKey;
            set => SetProperty(ref _yandexApiKey, value);
        }

        public string SelectedTranslationProvider
        {
            get => _selectedTranslationProvider;
            set => SetProperty(ref _selectedTranslationProvider, value);
        }

        public ObservableCollection<string> TranslationProviders
        {
            get => _translationProviders;
            set => SetProperty(ref _translationProviders, value);
        }

        // Capture Settings Properties
        public int CaptureDelay
        {
            get => _captureDelay;
            set => SetProperty(ref _captureDelay, value);
        }

        public bool CaptureCursor
        {
            get => _captureCursor;
            set => SetProperty(ref _captureCursor, value);
        }

        public string CaptureFormat
        {
            get => _captureFormat;
            set => SetProperty(ref _captureFormat, value);
        }

        // OCR Settings Properties
        public string SelectedOcrEngine
        {
            get => _selectedOcrEngine;
            set => SetProperty(ref _selectedOcrEngine, value);
        }

        public bool OcrPreprocessImage
        {
            get => _ocrPreprocessImage;
            set => SetProperty(ref _ocrPreprocessImage, value);
        }

        public int OcrScaleFactor
        {
            get => _ocrScaleFactor;
            set => SetProperty(ref _ocrScaleFactor, value);
        }

        // Overlay Settings Properties
        public double OverlayOpacity
        {
            get => _overlayOpacity;
            set => SetProperty(ref _overlayOpacity, value);
        }

        public int OverlayFontSize
        {
            get => _overlayFontSize;
            set => SetProperty(ref _overlayFontSize, value);
        }

        public string OverlayPosition
        {
            get => _overlayPosition;
            set => SetProperty(ref _overlayPosition, value);
        }

        public string OverlayFontFamily
        {
            get => _overlayFontFamily;
            set => SetProperty(ref _overlayFontFamily, value);
        }

        public string OverlayBackgroundColor
        {
            get => _overlayBackgroundColor;
            set => SetProperty(ref _overlayBackgroundColor, value);
        }

        public string OverlayTextColor
        {
            get => _overlayTextColor;
            set => SetProperty(ref _overlayTextColor, value);
        }

        public bool OverlayAlwaysOnTop
        {
            get => _overlayAlwaysOnTop;
            set => SetProperty(ref _overlayAlwaysOnTop, value);
        }

        public bool OverlayClickThrough
        {
            get => _overlayClickThrough;
            set => SetProperty(ref _overlayClickThrough, value);
        }

        // Hotkey Settings Properties
        public string CaptureHotkey
        {
            get => _captureHotkey;
            set => SetProperty(ref _captureHotkey, value);
        }

        public string TranslateHotkey
        {
            get => _translateHotkey;
            set => SetProperty(ref _translateHotkey, value);
        }

        public string ToggleOverlayHotkey
        {
            get => _toggleOverlayHotkey;
            set => SetProperty(ref _toggleOverlayHotkey, value);
        }

        public string HideOverlayHotkey
        {
            get => _hideOverlayHotkey;
            set => SetProperty(ref _hideOverlayHotkey, value);
        }

        // General Settings Properties
        public bool AutoStart
        {
            get => _autoStart;
            set => SetProperty(ref _autoStart, value);
        }

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetProperty(ref _minimizeToTray, value);
        }

        public bool ShowNotifications
        {
            get => _showNotifications;
            set => SetProperty(ref _showNotifications, value);
        }

        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set => SetProperty(ref _selectedLanguage, value);
        }

        public int CacheSize
        {
            get => _cacheSize;
            set => SetProperty(ref _cacheSize, value);
        }

        public int HistoryLimit
        {
            get => _historyLimit;
            set => SetProperty(ref _historyLimit, value);
        }

        public bool AutoTranslateAfterCapture
        {
            get => _autoTranslateAfterCapture;
            set => SetProperty(ref _autoTranslateAfterCapture, value);
        }

        public bool ShowTranslationInOverlay
        {
            get => _showTranslationInOverlay;
            set => SetProperty(ref _showTranslationInOverlay, value);
        }

        public ICommand SaveCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public ICommand ResetToDefaultsCommand { get; private set; }
        public ICommand TestApiCommand { get; private set; }
        public ICommand RecordHotkeyCommand { get; private set; }

        private void LoadSettings()
        {
            // API Settings
            GoogleApiKey = _appSettings.GoogleApiKey ?? string.Empty;
            DeeplApiKey = _appSettings.DeepLApiKey ?? string.Empty;
            YandexApiKey = _appSettings.YandexApiKey ?? string.Empty;
            SelectedTranslationProvider = _appSettings.TranslationProvider ?? "Google Translate";

            // Capture Settings
            CaptureDelay = _appSettings.CaptureDelay;
            CaptureCursor = _appSettings.CaptureCursor;
            CaptureFormat = _appSettings.CaptureFormat ?? "PNG";

            // OCR Settings
            SelectedOcrEngine = _appSettings.OcrEngine ?? "Tesseract";
            OcrPreprocessImage = _appSettings.OcrPreprocessImage;
            OcrScaleFactor = _appSettings.OcrScaleFactor;

            // Overlay Settings
            OverlayOpacity = _appSettings.OverlayOpacity;
            OverlayFontSize = _appSettings.OverlayFontSize;
            OverlayPosition = _appSettings.OverlayPosition ?? "BottomRight";
            OverlayFontFamily = _appSettings.OverlayFontFamily ?? "Segoe UI";
            OverlayBackgroundColor = _appSettings.OverlayBackgroundColor ?? "#CC000000";
            OverlayTextColor = _appSettings.OverlayTextColor ?? "#FFFFFFFF";
            OverlayAlwaysOnTop = _appSettings.OverlayAlwaysOnTop;
            OverlayClickThrough = _appSettings.OverlayClickThrough;

            // Hotkey Settings
            CaptureHotkey = _appSettings.CaptureHotkey ?? "Ctrl+Alt+Z";
            TranslateHotkey = _appSettings.TranslateHotkey ?? "Ctrl+Alt+X";
            ToggleOverlayHotkey = _appSettings.ToggleOverlayHotkey ?? "Ctrl+Alt+O";
            HideOverlayHotkey = _appSettings.HideOverlayHotkey ?? "Escape";

            // General Settings
            AutoStart = _appSettings.AutoStart;
            MinimizeToTray = _appSettings.MinimizeToTray;
            ShowNotifications = _appSettings.ShowNotifications;
            SelectedLanguage = _appSettings.UILanguage ?? "ru";
            CacheSize = _appSettings.CacheSize;
            HistoryLimit = _appSettings.HistoryLimit;
            AutoTranslateAfterCapture = _appSettings.AutoTranslateAfterCapture;
            ShowTranslationInOverlay = _appSettings.ShowTranslationInOverlay;
        }

        private void RegisterCommands()
        {
            SaveCommand = new RelayCommand(_ => ExecuteSave(), _ => true);
            CancelCommand = new RelayCommand(_ => ExecuteCancel(), _ => true);
            ResetToDefaultsCommand = new RelayCommand(_ => ExecuteResetToDefaults(), _ => true);
            TestApiCommand = new RelayCommand(async _ => await ExecuteTestApiAsync(), _ => true);
            RecordHotkeyCommand = new RelayCommand(param => ExecuteRecordHotkey(param), _ => true);
        }

        private void ExecuteSave()
        {
            // API Settings
            _appSettings.GoogleApiKey = GoogleApiKey;
            _appSettings.DeepLApiKey = DeeplApiKey;
            _appSettings.YandexApiKey = YandexApiKey;
            _appSettings.TranslationProvider = SelectedTranslationProvider;

            // Capture Settings
            _appSettings.CaptureDelay = CaptureDelay;
            _appSettings.CaptureCursor = CaptureCursor;
            _appSettings.CaptureFormat = CaptureFormat;

            // OCR Settings
            _appSettings.OcrEngine = SelectedOcrEngine;
            _appSettings.OcrPreprocessImage = OcrPreprocessImage;
            _appSettings.OcrScaleFactor = OcrScaleFactor;

            // Overlay Settings
            _appSettings.OverlayOpacity = OverlayOpacity;
            _appSettings.OverlayFontSize = OverlayFontSize;
            _appSettings.OverlayPosition = OverlayPosition;
            _appSettings.OverlayFontFamily = OverlayFontFamily;
            _appSettings.OverlayBackgroundColor = OverlayBackgroundColor;
            _appSettings.OverlayTextColor = OverlayTextColor;
            _appSettings.OverlayAlwaysOnTop = OverlayAlwaysOnTop;
            _appSettings.OverlayClickThrough = OverlayClickThrough;

            // Hotkey Settings
            _appSettings.CaptureHotkey = CaptureHotkey;
            _appSettings.TranslateHotkey = TranslateHotkey;
            _appSettings.ToggleOverlayHotkey = ToggleOverlayHotkey;
            _appSettings.HideOverlayHotkey = HideOverlayHotkey;

            // General Settings
            _appSettings.AutoStart = AutoStart;
            _appSettings.MinimizeToTray = MinimizeToTray;
            _appSettings.ShowNotifications = ShowNotifications;
            _appSettings.UILanguage = SelectedLanguage;
            _appSettings.CacheSize = CacheSize;
            _appSettings.HistoryLimit = HistoryLimit;
            _appSettings.AutoTranslateAfterCapture = AutoTranslateAfterCapture;
            _appSettings.ShowTranslationInOverlay = ShowTranslationInOverlay;

            // Сохранение настроек
            _appSettings.Save();

            // Обновление хоткеев
            _hotkeyService.UnregisterAll();
            _hotkeyService.RegisterHotkey(CaptureHotkey, () => { });
            _hotkeyService.RegisterHotkey(TranslateHotkey, () => { });
            _hotkeyService.RegisterHotkey(ToggleOverlayHotkey, () => { });
            _hotkeyService.RegisterHotkey(HideOverlayHotkey, () => { });

            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }

        private void ExecuteCancel()
        {
            LoadSettings();
            SettingsCancelled?.Invoke(this, EventArgs.Empty);
        }

        private void ExecuteResetToDefaults()
        {
            GoogleApiKey = string.Empty;
            DeeplApiKey = string.Empty;
            YandexApiKey = string.Empty;
            SelectedTranslationProvider = "Google Translate";
            CaptureDelay = 500;
            CaptureCursor = false;
            CaptureFormat = "PNG";
            SelectedOcrEngine = "Tesseract";
            OcrPreprocessImage = true;
            OcrScaleFactor = 2;
            OverlayOpacity = 0.8;
            OverlayFontSize = 14;
            OverlayPosition = "BottomRight";
            OverlayFontFamily = "Segoe UI";
            OverlayBackgroundColor = "#CC000000";
            OverlayTextColor = "#FFFFFFFF";
            OverlayAlwaysOnTop = true;
            OverlayClickThrough = true;
            CaptureHotkey = "Ctrl+Alt+Z";
            TranslateHotkey = "Ctrl+Alt+X";
            ToggleOverlayHotkey = "Ctrl+Alt+O";
            HideOverlayHotkey = "Escape";
            AutoStart = false;
            MinimizeToTray = true;
            ShowNotifications = true;
            SelectedLanguage = "ru";
            CacheSize = 1000;
            HistoryLimit = 100;
            AutoTranslateAfterCapture = false;
            ShowTranslationInOverlay = true;
        }

        private async Task ExecuteTestApiAsync()
        {
            try
            {
                TestApiStatus = "Проверка...";
                IsTestingApi = true;

                var result = await _translationService.TestConnectionAsync(SelectedTranslationProvider);
                
                if (result)
                {
                    TestApiStatus = "Соединение успешно!";
                    TestApiSuccess = true;
                }
                else
                {
                    TestApiStatus = "Ошибка соединения";
                    TestApiSuccess = false;
                }
            }
            catch (Exception ex)
            {
                TestApiStatus = $"Ошибка: {ex.Message}";
                TestApiSuccess = false;
            }
            finally
            {
                IsTestingApi = false;
            }
        }

        private void ExecuteRecordHotkey(object parameter)
        {
            // Логика записи хоткея
            var hotkeyName = parameter as string;
            // Реализация через отдельный сервис
        }

        private string _testApiStatus;
        private bool _isTestingApi;
        private bool _testApiSuccess;

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

        public bool TestApiSuccess
        {
            get => _testApiSuccess;
            set => SetProperty(ref _testApiSuccess, value);
        }

        public event EventHandler SettingsSaved;
        public event EventHandler SettingsCancelled;
    }
}
