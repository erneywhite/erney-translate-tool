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

// Note: LanguageManager is in ErneyTranslateTool.Core, already in scope above.

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

        StatusMessage = LanguageManager.Format("Strings.Glossary.LoadedFmt", Entries.Count);
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
        StatusMessage = LanguageManager.Get("Strings.Glossary.AddedHint");
    }

    private void Delete(GlossaryEntry? entry)
    {
        if (entry == null) return;
        if (MessageBox.Show(
                LanguageManager.Format("Strings.Glossary.DeleteBodyFmt", entry.SourceText, entry.TargetText),
                LanguageManager.Get("Strings.Glossary.DeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        if (_repo.Delete(entry.Id))
        {
            Entries.Remove(entry);
            _applier.Invalidate();
            StatusMessage = LanguageManager.Get("Strings.Glossary.DeletedHint");
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
        StatusMessage = LanguageManager.Format("Strings.Glossary.SavedFmt", ok);
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Filter = LanguageManager.Get("Strings.Glossary.ImportFilter"),
            Title = LanguageManager.Get("Strings.Glossary.ImportTitle"),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<List<GlossaryEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null || imported.Count == 0)
            {
                StatusMessage = LanguageManager.Get("Strings.Glossary.ImportEmptyHint");
                return;
            }

            // Reset Id so SQLite assigns fresh ones (otherwise we'd collide
            // with existing rules).
            foreach (var e in imported) { e.Id = 0; _repo.Add(e); }
            _applier.Invalidate();
            Refresh();
            StatusMessage = LanguageManager.Format("Strings.Glossary.ImportedFmt", imported.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary import failed");
            MessageBox.Show(
                LanguageManager.Format("Strings.Glossary.ImportErrorFmt", ex.Message),
                LanguageManager.Get("Strings.Glossary.ImportErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = LanguageManager.Get("Strings.Glossary.ImportFilter"),
            FileName = $"glossary-export-{DateTime.Now:yyyy-MM-dd}.json",
            Title = LanguageManager.Get("Strings.Glossary.ExportTitle"),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = JsonSerializer.Serialize(Entries.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusMessage = LanguageManager.Format("Strings.Glossary.ExportedFmt",
                Entries.Count, Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary export failed");
            MessageBox.Show(
                LanguageManager.Format("Strings.Glossary.ExportErrorFmt", ex.Message),
                LanguageManager.Get("Strings.Glossary.ExportErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
