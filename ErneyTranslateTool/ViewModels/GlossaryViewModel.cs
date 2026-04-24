using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Glossary;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Microsoft.Win32;
using Serilog;

namespace ErneyTranslateTool.ViewModels;

/// <summary>
/// Backs the Glossary tab. CRUD over <see cref="GlossaryRepository"/>,
/// import/export via JSON, and a master toggle bound to AppSettings.
/// </summary>
public class GlossaryViewModel : BaseViewModel
{
    private readonly GlossaryRepository _repo;
    private readonly GlossaryApplier _applier;
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public ObservableCollection<GlossaryEntry> Entries { get; } = new();

    public GlossaryViewModel(GlossaryRepository repo, GlossaryApplier applier,
        AppSettings settings, ILogger logger)
    {
        _repo = repo;
        _applier = applier;
        _settings = settings;
        _logger = logger;

        AddCommand = new RelayCommand(_ => Add());
        DeleteCommand = new RelayCommand(p => Delete(p as GlossaryEntry));
        SaveCommand = new RelayCommand(_ => SaveAll());
        RefreshCommand = new RelayCommand(_ => Refresh());
        ImportCommand = new RelayCommand(_ => Import());
        ExportCommand = new RelayCommand(_ => Export());

        Refresh();
    }

    private bool _glossaryEnabled;
    /// <summary>Master kill-switch — bound to AppSettings.GlossaryEnabled.</summary>
    public bool GlossaryEnabled
    {
        get => _glossaryEnabled;
        set
        {
            if (SetProperty(ref _glossaryEnabled, value))
            {
                _settings.Config.GlossaryEnabled = value;
                _settings.Save();
                _applier.Invalidate();
            }
        }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var e in _repo.GetAll())
            Entries.Add(e);

        // Pull master toggle without triggering the setter (no save loop).
        _glossaryEnabled = _settings.Config.GlossaryEnabled;
        OnPropertyChanged(nameof(GlossaryEnabled));

        StatusMessage = $"Загружено правил: {Entries.Count}";
    }

    private void Add()
    {
        var entry = new GlossaryEntry
        {
            SourceText = "",
            TargetText = "",
            TargetLanguage = string.IsNullOrWhiteSpace(_settings.Config.TargetLanguage)
                ? "RU" : _settings.Config.TargetLanguage,
            IsCaseSensitive = false,
            IsWholeWord = true,
            Notes = string.Empty,
        };
        // Insert immediately so Id is assigned and edits propagate — the
        // user can fill source/target inline in the DataGrid.
        _repo.Add(entry);
        Entries.Add(entry);
        _applier.Invalidate();
        StatusMessage = "Добавлено пустое правило — заполни поля «Оригинал» и «Перевод», затем «Сохранить».";
    }

    private void Delete(GlossaryEntry? entry)
    {
        if (entry == null) return;
        if (MessageBox.Show(
                $"Удалить правило «{entry.SourceText}» → «{entry.TargetText}»?",
                "Удаление правила", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        if (_repo.Delete(entry.Id))
        {
            Entries.Remove(entry);
            _applier.Invalidate();
            StatusMessage = "Правило удалено.";
        }
    }

    private void SaveAll()
    {
        // The DataGrid edits the entries in-place; we just need to persist
        // each one. Bulk-update is fine with a few hundred rules.
        var ok = 0;
        foreach (var e in Entries)
        {
            if (e.Id == 0) _repo.Add(e);
            else if (_repo.Update(e)) ok++;
        }
        _applier.Invalidate();
        StatusMessage = $"Сохранено правил: {ok}.";
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON-файл глоссария (*.json)|*.json",
            Title = "Импорт глоссария",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<List<GlossaryEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null || imported.Count == 0)
            {
                StatusMessage = "Файл пуст или не содержит правил.";
                return;
            }

            // Reset Id so SQLite assigns fresh ones (otherwise we'd collide
            // with existing rules).
            foreach (var e in imported) { e.Id = 0; _repo.Add(e); }
            _applier.Invalidate();
            Refresh();
            StatusMessage = $"Импортировано правил: {imported.Count}.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary import failed");
            MessageBox.Show($"Не удалось импортировать: {ex.Message}",
                "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON-файл глоссария (*.json)|*.json",
            FileName = $"glossary-export-{DateTime.Now:yyyy-MM-dd}.json",
            Title = "Экспорт глоссария",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = JsonSerializer.Serialize(Entries.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusMessage = $"Экспортировано правил: {Entries.Count} в {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary export failed");
            MessageBox.Show($"Не удалось экспортировать: {ex.Message}",
                "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
