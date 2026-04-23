using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ErneyTranslateTool.Models;
using ErneyTranslateTool.Data;
using Microsoft.Win32;

namespace ErneyTranslateTool.ViewModels;

public class HistoryViewModel : BaseViewModel
{
    private readonly HistoryRepository _historyRepository;
    private SessionHistory? _selectedSession;
    private string _searchQuery = string.Empty;

    public ObservableCollection<SessionHistory> Sessions { get; } = new();
    public ObservableCollection<TranslationEntry> SessionEntries { get; } = new();

    public HistoryViewModel(HistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;

        RefreshCommand = new RelayCommand(_ => Refresh());
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory());
        ExportSessionCommand = new RelayCommand(_ => ExportSession(), _ => SelectedSession != null);
        SearchCommand = new RelayCommand(_ => DoSearch());

        Refresh();
    }

    public SessionHistory? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                LoadSessionEntries();
                OnPropertyChanged(nameof(HasSelectedSession));
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public bool HasSelectedSession => SelectedSession != null;

    public ICommand RefreshCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ExportSessionCommand { get; }
    public ICommand SearchCommand { get; }

    public void Refresh()
    {
        Sessions.Clear();
        foreach (var s in _historyRepository.GetSessions())
            Sessions.Add(s);
        if (SelectedSession != null && !Sessions.Any(s => s.Id == SelectedSession.Id))
        {
            SelectedSession = null;
            SessionEntries.Clear();
        }
    }

    private void LoadSessionEntries()
    {
        SessionEntries.Clear();
        if (SelectedSession == null) return;
        foreach (var e in _historyRepository.GetSessionTranslations(SelectedSession.Id))
            SessionEntries.Add(e);
    }

    private void DoSearch()
    {
        SessionEntries.Clear();
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            LoadSessionEntries();
            return;
        }
        foreach (var e in _historyRepository.SearchTranslations(SearchQuery))
            SessionEntries.Add(e);
    }

    private void ClearHistory()
    {
        var result = MessageBox.Show(
            "Удалить всю историю переводов?",
            "Подтверждение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _historyRepository.ClearHistory();
        Sessions.Clear();
        SessionEntries.Clear();
        SelectedSession = null;
    }

    private void ExportSession()
    {
        if (SelectedSession == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Текст (*.txt)|*.txt",
            FileName = $"session_{SelectedSession.Id}_{SelectedSession.StartTime:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var entries = _historyRepository.GetSessionTranslations(SelectedSession.Id);
        var sb = new StringBuilder();
        var isCsv = dialog.FilterIndex == 1;

        if (isCsv)
        {
            sb.AppendLine("Время;Язык;Оригинал;Перевод");
            foreach (var e in entries)
            {
                sb.AppendLine(string.Join(";",
                    e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    Csv(e.SourceLanguage),
                    Csv(e.OriginalText),
                    Csv(e.TranslatedText)));
            }
        }
        else
        {
            foreach (var e in entries)
            {
                sb.AppendLine($"[{e.Timestamp:HH:mm:ss}] [{e.SourceLanguage}]");
                sb.AppendLine(e.OriginalText);
                sb.AppendLine("→ " + e.TranslatedText);
                sb.AppendLine();
            }
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show("Экспорт завершён.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Csv(string s) => "\"" + (s ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
