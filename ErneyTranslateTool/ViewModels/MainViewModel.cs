using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ErneyTranslateTool.Services;
using ErneyTranslateTool.Repositories;
using ErneyTranslateTool.Models;
using ErneyTranslateTool.Core;

namespace ErneyTranslateTool.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly ICaptureService _captureService;
        private readonly IOcrService _ocrService;
        private readonly ITranslationService _translationService;
        private readonly IOverlayManager _overlayManager;
        private readonly IHotkeyService _hotkeyService;
        private readonly ICacheRepository _cacheRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly AppSettings _appSettings;

        private string _capturedText;
        private string _translatedText;
        private string _statusMessage;
        private bool _isTranslating;
        private bool _overlayVisible;
        private string _selectedSourceLanguage;
        private string _selectedTargetLanguage;
        private ObservableCollection<LanguageInfo> _availableLanguages;
        private TranslationHistoryItem _lastTranslation;

        public MainViewModel(
            ICaptureService captureService,
            IOcrService ocrService,
            ITranslationService translationService,
            IOverlayManager overlayManager,
            IHotkeyService hotkeyService,
            ICacheRepository cacheRepository,
            IHistoryRepository historyRepository,
            AppSettings appSettings)
        {
            _captureService = captureService;
            _ocrService = ocrService;
            _translationService = translationService;
            _overlayManager = overlayManager;
            _hotkeyService = hotkeyService;
            _cacheRepository = cacheRepository;
            _historyRepository = historyRepository;
            _appSettings = appSettings;

            _availableLanguages = new ObservableCollection<LanguageInfo>();
            _capturedText = string.Empty;
            _translatedText = string.Empty;
            _statusMessage = "Готов к работе";
            _isTranslating = false;
            _overlayVisible = false;

            InitializeLanguages();
            RegisterCommands();
            RegisterHotkeys();
            LoadSettings();
        }

        public string CapturedText
        {
            get => _capturedText;
            set => SetProperty(ref _capturedText, value);
        }

        public string TranslatedText
        {
            get => _translatedText;
            set => SetProperty(ref _translatedText, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsTranslating
        {
            get => _isTranslating;
            set => SetProperty(ref _isTranslating, value);
        }

        public bool OverlayVisible
        {
            get => _overlayVisible;
            set
            {
                if (SetProperty(ref _overlayVisible, value))
                {
                    OnPropertyChanged(nameof(CanHideOverlay));
                }
            }
        }

        public bool CanHideOverlay => _overlayVisible;

        public string SelectedSourceLanguage
        {
            get => _selectedSourceLanguage;
            set
            {
                if (SetProperty(ref _selectedSourceLanguage, value))
                {
                    _appSettings.SourceLanguageCode = value;
                    OnPropertyChanged(nameof(CanTranslate));
                }
            }
        }

        public string SelectedTargetLanguage
        {
            get => _selectedTargetLanguage;
            set
            {
                if (SetProperty(ref _selectedTargetLanguage, value))
                {
                    _appSettings.TargetLanguageCode = value;
                    OnPropertyChanged(nameof(CanTranslate));
                }
            }
        }

        public ObservableCollection<LanguageInfo> AvailableLanguages
        {
            get => _availableLanguages;
            set => SetProperty(ref _availableLanguages, value);
        }

        public TranslationHistoryItem LastTranslation
        {
            get => _lastTranslation;
            set => SetProperty(ref _lastTranslation, value);
        }

        public bool CanTranslate => !string.IsNullOrEmpty(_capturedText) && 
                                     !string.IsNullOrEmpty(_selectedSourceLanguage) && 
                                     !string.IsNullOrEmpty(_selectedTargetLanguage) && 
                                     !_isTranslating;

        public bool CanCopyTranslatedText => !string.IsNullOrEmpty(_translatedText);

        public bool CanClearText => !string.IsNullOrEmpty(_capturedText) || !string.IsNullOrEmpty(_translatedText);

        public ICommand CaptureCommand { get; private set; }
        public ICommand TranslateCommand { get; private set; }
        public ICommand CopyTranslatedCommand { get; private set; }
        public ICommand ClearTextCommand { get; private set; }
        public ICommand ToggleOverlayCommand { get; private set; }
        public ICommand HideOverlayCommand { get; private set; }
        public ICommand OpenSettingsCommand { get; private set; }
        public ICommand OpenHistoryCommand { get; private set; }

        private void InitializeLanguages()
        {
            var languages = _translationService.GetSupportedLanguages();
            foreach (var lang in languages)
            {
                _availableLanguages.Add(lang);
            }

            if (_availableLanguages.Count > 0)
            {
                SelectedSourceLanguage = _appSettings.SourceLanguageCode ?? "auto";
                SelectedTargetLanguage = _appSettings.TargetLanguageCode ?? _availableLanguages[0].Code;
            }
        }

        private void RegisterCommands()
        {
            CaptureCommand = new RelayCommand(async _ => await ExecuteCaptureAsync(), _ => true);
            TranslateCommand = new RelayCommand(async _ => await ExecuteTranslateAsync(), _ => CanTranslate);
            CopyTranslatedCommand = new RelayCommand(_ => ExecuteCopyTranslated(), _ => CanCopyTranslatedText);
            ClearTextCommand = new RelayCommand(_ => ExecuteClearText(), _ => CanClearText);
            ToggleOverlayCommand = new RelayCommand(_ => ExecuteToggleOverlay(), _ => true);
            HideOverlayCommand = new RelayCommand(_ => ExecuteHideOverlay(), _ => CanHideOverlay);
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings(), _ => true);
            OpenHistoryCommand = new RelayCommand(_ => OpenHistory(), _ => true);
        }

        private void RegisterHotkeys()
        {
            _hotkeyService.RegisterHotkey(
                _appSettings.CaptureHotkey,
                () => ExecuteCaptureAsync().FireAndForgetSafeAsync());

            _hotkeyService.RegisterHotkey(
                _appSettings.TranslateHotkey,
                () => ExecuteTranslateAsync().FireAndForgetSafeAsync());

            _hotkeyService.RegisterHotkey(
                _appSettings.ToggleOverlayHotkey,
                () => ExecuteToggleOverlay());

            _hotkeyService.RegisterHotkey(
                _appSettings.HideOverlayHotkey,
                () => ExecuteHideOverlay());
        }

        private void LoadSettings()
        {
            _overlayManager.OverlayOpacity = _appSettings.OverlayOpacity;
            _overlayManager.OverlayFontSize = _appSettings.OverlayFontSize;
            _overlayManager.OverlayPosition = _appSettings.OverlayPosition;
        }

        private async Task ExecuteCaptureAsync()
        {
            try
            {
                StatusMessage = "Захват области...";
                
                var screenshot = await _captureService.CaptureRegionAsync();
                if (screenshot == null)
                {
                    StatusMessage = "Захват отменён";
                    return;
                }

                StatusMessage = "Распознавание текста...";
                var ocrResult = await _ocrService.RecognizeTextAsync(screenshot);

                if (string.IsNullOrWhiteSpace(ocrResult))
                {
                    StatusMessage = "Текст не найден";
                    CapturedText = string.Empty;
                    return;
                }

                CapturedText = ocrResult;
                StatusMessage = "Текст распознан. Готов к переводу.";

                // Автоперевод если включено
                if (_appSettings.AutoTranslateAfterCapture)
                {
                    await ExecuteTranslateAsync();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка захвата: {ex.Message}";
            }
        }

        private async Task ExecuteTranslateAsync()
        {
            if (!CanTranslate) return;

            try
            {
                IsTranslating = true;
                StatusMessage = "Перевод...";

                // Проверка кэша
                var cacheKey = $"{_capturedText}|{SelectedSourceLanguage}|{SelectedTargetLanguage}";
                var cached = await _cacheRepository.GetTranslationAsync(cacheKey);
                
                if (cached != null)
                {
                    TranslatedText = cached;
                    StatusMessage = "Перевод загружен из кэша";
                    IsTranslating = false;
                    return;
                }

                // Выполнение перевода
                var translation = await _translationService.TranslateAsync(
                    _capturedText,
                    SelectedSourceLanguage,
                    SelectedTargetLanguage);

                TranslatedText = translation;

                // Сохранение в кэш
                await _cacheRepository.SaveTranslationAsync(cacheKey, translation);

                // Сохранение в историю
                var historyItem = new TranslationHistoryItem
                {
                    SourceText = _capturedText,
                    TranslatedText = translation,
                    SourceLanguage = SelectedSourceLanguage,
                    TargetLanguage = SelectedTargetLanguage,
                    Timestamp = DateTime.Now
                };
                await _historyRepository.AddAsync(historyItem);
                LastTranslation = historyItem;

                StatusMessage = "Перевод выполнен успешно";

                // Показ в оверлее если включено
                if (_appSettings.ShowTranslationInOverlay)
                {
                    await _overlayManager.ShowTranslationAsync(translation);
                    OverlayVisible = true;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка перевода: {ex.Message}";
            }
            finally
            {
                IsTranslating = false;
            }
        }

        private void ExecuteCopyTranslated()
        {
            if (!string.IsNullOrEmpty(_translatedText))
            {
                System.Windows.Clipboard.SetText(_translatedText);
                StatusMessage = "Перевод скопирован в буфер обмена";
            }
        }

        private void ExecuteClearText()
        {
            CapturedText = string.Empty;
            TranslatedText = string.Empty;
            StatusMessage = "Текст очищен";
        }

        private void ExecuteToggleOverlay()
        {
            OverlayVisible = !OverlayVisible;
            
            if (OverlayVisible && !string.IsNullOrEmpty(_translatedText))
            {
                _overlayManager.ShowTranslationAsync(_translatedText).FireAndForgetSafeAsync();
            }
            else
            {
                _overlayManager.HideAsync().FireAndForgetSafeAsync();
            }
        }

        private void ExecuteHideOverlay()
        {
            OverlayVisible = false;
            _overlayManager.HideAsync().FireAndForgetSafeAsync();
        }

        private void OpenSettings()
        {
            // Событие для открытия окна настроек
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenHistory()
        {
            // Событие для открытия окна истории
            HistoryRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler SettingsRequested;
        public event EventHandler HistoryRequested;

        public void SaveCurrentSettings()
        {
            _appSettings.SourceLanguageCode = SelectedSourceLanguage;
            _appSettings.TargetLanguageCode = SelectedTargetLanguage;
        }

        public void ReloadHotkeys()
        {
            _hotkeyService.UnregisterAll();
            RegisterHotkeys();
        }
    }
}
