using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ErneyTranslateTool.Models;
using ErneyTranslateTool.Repositories;

namespace ErneyTranslateTool.ViewModels
{
    public class HistoryViewModel : BaseViewModel
    {
        private readonly IHistoryRepository _historyRepository;

        private ObservableCollection<TranslationHistoryItem> _historyItems;
        private TranslationHistoryItem _selectedItem;
        private string _searchQuery;
        private string _filterSourceLanguage;
        private string _filterTargetLanguage;
        private DateTime? _filterFromDate;
        private DateTime? _filterToDate;
        private int _pageSize;
        private int _currentPage;
        private int _totalPages;
        private int _totalItems;
        private bool _isLoading;

        public HistoryViewModel(IHistoryRepository historyRepository)
        {
            _historyRepository = historyRepository;

            _historyItems = new ObservableCollection<TranslationHistoryItem>();
            _searchQuery = string.Empty;
            _pageSize = 20;
            _currentPage = 1;

            InitializeFilters();
            RegisterCommands();
            LoadHistoryAsync().FireAndForgetSafeAsync();
        }

        public ObservableCollection<TranslationHistoryItem> HistoryItems
        {
            get => _historyItems;
            set => SetProperty(ref _historyItems, value);
        }

        public TranslationHistoryItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetProperty(ref _selectedItem, value))
                {
                    OnPropertyChanged(nameof(CanCopySourceText));
                    OnPropertyChanged(nameof(CanCopyTranslatedText));
                    OnPropertyChanged(nameof(CanDeleteItem));
                }
            }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    SearchCommand.Execute(null);
                }
            }
        }

        public string FilterSourceLanguage
        {
            get => _filterSourceLanguage;
            set
            {
                if (SetProperty(ref _filterSourceLanguage, value))
                {
                    ApplyFiltersCommand.Execute(null);
                }
            }
        }

        public string FilterTargetLanguage
        {
            get => _filterTargetLanguage;
            set
            {
                if (SetProperty(ref _filterTargetLanguage, value))
                {
                    ApplyFiltersCommand.Execute(null);
                }
            }
        }

        public DateTime? FilterFromDate
        {
            get => _filterFromDate;
            set
            {
                if (SetProperty(ref _filterFromDate, value))
                {
                    ApplyFiltersCommand.Execute(null);
                }
            }
        }

        public DateTime? FilterToDate
        {
            get => _filterToDate;
            set
            {
                if (SetProperty(ref _filterToDate, value))
                {
                    ApplyFiltersCommand.Execute(null);
                }
            }
        }

        public int PageSize
        {
            get => _pageSize;
            set
            {
                if (SetProperty(ref _pageSize, value))
                {
                    _currentPage = 1;
                    LoadHistoryAsync().FireAndForgetSafeAsync();
                }
            }
        }

        public int CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    LoadHistoryAsync().FireAndForgetSafeAsync();
                }
            }
        }

        public int TotalPages
        {
            get => _totalPages;
            set => SetProperty(ref _totalPages, value);
        }

        public int TotalItems
        {
            get => _totalItems;
            set => SetProperty(ref _totalItems, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public bool CanCopySourceText => SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.SourceText);
        public bool CanCopyTranslatedText => SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.TranslatedText);
        public bool CanDeleteItem => SelectedItem != null;
        public bool CanClearHistory => _historyItems.Count > 0;
        public bool CanGoToPreviousPage => _currentPage > 1;
        public bool CanGoToNextPage => _currentPage < _totalPages;

        public ICommand LoadHistoryCommand { get; private set; }
        public ICommand SearchCommand { get; private set; }
        public ICommand ApplyFiltersCommand { get; private set; }
        public ICommand ClearFiltersCommand { get; private set; }
        public ICommand CopySourceTextCommand { get; private set; }
        public ICommand CopyTranslatedTextCommand { get; private set; }
        public ICommand DeleteItemCommand { get; private set; }
        public ICommand ClearHistoryCommand { get; private set; }
        public ICommand ExportHistoryCommand { get; private set; }
        public ICommand GoToPreviousPageCommand { get; private set; }
        public ICommand GoToNextPageCommand { get; private set; }
        public ICommand GoToFirstPageCommand { get; private set; }
        public ICommand GoToLastPageCommand { get; private set; }

        public ObservableCollection<string> SourceLanguageFilters { get; private set; }
        public ObservableCollection<string> TargetLanguageFilters { get; private set; }

        private void InitializeFilters()
        {
            SourceLanguageFilters = new ObservableCollection<string> { "Все языки" };
            TargetLanguageFilters = new ObservableCollection<string> { "Все языки" };

            FilterSourceLanguage = "Все языки";
            FilterTargetLanguage = "Все языки";
        }

        private void RegisterCommands()
        {
            LoadHistoryCommand = new RelayCommand(async _ => await LoadHistoryAsync(), _ => true);
            SearchCommand = new RelayCommand(async _ => await SearchAsync(), _ => true);
            ApplyFiltersCommand = new RelayCommand(async _ => await ApplyFiltersAsync(), _ => true);
            ClearFiltersCommand = new RelayCommand(_ => ExecuteClearFilters(), _ => true);
            CopySourceTextCommand = new RelayCommand(_ => ExecuteCopySourceText(), _ => CanCopySourceText);
            CopyTranslatedTextCommand = new RelayCommand(_ => ExecuteCopyTranslatedText(), _ => CanCopyTranslatedText);
            DeleteItemCommand = new RelayCommand(async _ => await DeleteItemAsync(), _ => CanDeleteItem);
            ClearHistoryCommand = new RelayCommand(async _ => await ClearHistoryAsync(), _ => CanClearHistory);
            ExportHistoryCommand = new RelayCommand(async _ => await ExportHistoryAsync(), _ => true);
            GoToPreviousPageCommand = new RelayCommand(_ => CurrentPage--, _ => CanGoToPreviousPage);
            GoToNextPageCommand = new RelayCommand(_ => CurrentPage++, _ => CanGoToNextPage);
            GoToFirstPageCommand = new RelayCommand(_ => CurrentPage = 1, _ => CanGoToPreviousPage);
            GoToLastPageCommand = new RelayCommand(_ => CurrentPage = _totalPages, _ => CanGoToNextPage);
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                IsLoading = true;

                var items = await _historyRepository.GetPagedAsync(
                    _currentPage,
                    _pageSize,
                    _searchQuery,
                    _filterSourceLanguage != "Все языки" ? _filterSourceLanguage : null,
                    _filterTargetLanguage != "Все языки" ? _filterTargetLanguage : null,
                    _filterFromDate,
                    _filterToDate);

                HistoryItems.Clear();
                foreach (var item in items.Items)
                {
                    HistoryItems.Add(item);
                }

                TotalItems = items.TotalCount;
                TotalPages = (int)Math.Ceiling((double)TotalItems / _pageSize);

                UpdateLanguageFilters();
            }
            catch (Exception ex)
            {
                // Логирование ошибки
                System.Diagnostics.Debug.WriteLine($"Ошибка загрузки истории: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task SearchAsync()
        {
            _currentPage = 1;
            await LoadHistoryAsync();
        }

        private async Task ApplyFiltersAsync()
        {
            _currentPage = 1;
            await LoadHistoryAsync();
        }

        private void ExecuteClearFilters()
        {
            SearchQuery = string.Empty;
            FilterSourceLanguage = "Все языки";
            FilterTargetLanguage = "Все языки";
            FilterFromDate = null;
            FilterToDate = null;
        }

        private void ExecuteCopySourceText()
        {
            if (SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.SourceText))
            {
                System.Windows.Clipboard.SetText(SelectedItem.SourceText);
            }
        }

        private void ExecuteCopyTranslatedText()
        {
            if (SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.TranslatedText))
            {
                System.Windows.Clipboard.SetText(SelectedItem.TranslatedText);
            }
        }

        private async Task DeleteItemAsync()
        {
            if (SelectedItem == null) return;

            try
            {
                await _historyRepository.DeleteAsync(SelectedItem.Id);
                HistoryItems.Remove(SelectedItem);
                TotalItems--;
                TotalPages = (int)Math.Ceiling((double)TotalItems / _pageSize);
                SelectedItem = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка удаления: {ex.Message}");
            }
        }

        private async Task ClearHistoryAsync()
        {
            try
            {
                await _historyRepository.ClearAsync();
                HistoryItems.Clear();
                TotalItems = 0;
                TotalPages = 0;
                CurrentPage = 1;
                SelectedItem = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка очистки истории: {ex.Message}");
            }
        }

        private async Task ExportHistoryAsync()
        {
            try
            {
                var allItems = await _historyRepository.GetAllAsync();
                
                // Экспорт в CSV
                var csvContent = "Дата;Исходный язык;Целевой язык;Исходный текст;Перевод\n";
                foreach (var item in allItems)
                {
                    csvContent += $"{item.Timestamp:yyyy-MM-dd HH:mm:ss};" +
                                  $"{item.SourceLanguage};" +
                                  $"{item.TargetLanguage};" +
                                  $"\"{EscapeCsv(item.SourceText)}\";" +
                                  $"\"{EscapeCsv(item.TranslatedText)}\"\n";
                }

                // Сохранение файла через диалог
                // Реализуется через сервис диалогов
                ExportRequested?.Invoke(this, new ExportEventArgs(csvContent));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка экспорта: {ex.Message}");
            }
        }

        private string EscapeCsv(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\"", "\"\"");
        }

        private void UpdateLanguageFilters()
        {
            var sourceLangs = new HashSet<string>();
            var targetLangs = new HashSet<string>();

            foreach (var item in HistoryItems)
            {
                if (!string.IsNullOrEmpty(item.SourceLanguage))
                    sourceLangs.Add(item.SourceLanguage);
                if (!string.IsNullOrEmpty(item.TargetLanguage))
                    targetLangs.Add(item.TargetLanguage);
            }

            // Сохранение текущего выбора
            var currentSource = FilterSourceLanguage;
            var currentTarget = FilterTargetLanguage;

            SourceLanguageFilters.Clear();
            SourceLanguageFilters.Add("Все языки");
            foreach (var lang in sourceLangs)
            {
                SourceLanguageFilters.Add(lang);
            }

            TargetLanguageFilters.Clear();
            TargetLanguageFilters.Add("Все языки");
            foreach (var lang in targetLangs)
            {
                TargetLanguageFilters.Add(lang);
            }

            // Восстановление выбора если возможно
            if (SourceLanguageFilters.Contains(currentSource))
                FilterSourceLanguage = currentSource;
            if (TargetLanguageFilters.Contains(currentTarget))
                FilterTargetLanguage = currentTarget;
        }

        public event EventHandler<ExportEventArgs> ExportRequested;

        public void Refresh()
        {
            LoadHistoryAsync().FireAndForgetSafeAsync();
        }

        public void AddItem(TranslationHistoryItem item)
        {
            HistoryItems.Insert(0, item);
            TotalItems++;
            TotalPages = (int)Math.Ceiling((double)TotalItems / _pageSize);
        }
    }

    public class ExportEventArgs : EventArgs
    {
        public string Content { get; }

        public ExportEventArgs(string content)
        {
            Content = content;
        }
    }
}
