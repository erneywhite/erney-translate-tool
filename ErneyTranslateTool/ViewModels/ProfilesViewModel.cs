using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Profiles;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Microsoft.Win32;
using Serilog;

namespace ErneyTranslateTool.ViewModels;

/// <summary>
/// Backs the Profiles tab. CRUD over <see cref="GameProfileRepository"/>,
/// "create from current settings" shortcut, "apply now" + import/export.
/// </summary>
public class ProfilesViewModel : BaseViewModel
{
    private readonly GameProfileRepository _repo;
    private readonly ProfileManager _manager;
    private readonly ILogger _logger;

    public ObservableCollection<GameProfile> Profiles { get; } = new();

    public ProfilesViewModel(GameProfileRepository repo, ProfileManager manager, ILogger logger)
    {
        _repo = repo;
        _manager = manager;
        _logger = logger;

        AddBlankCommand = new RelayCommand(_ => AddBlank());
        CreateFromCurrentCommand = new RelayCommand(_ => CreateFromCurrent());
        DeleteCommand = new RelayCommand(p => Delete(p as GameProfile));
        SaveCommand = new RelayCommand(_ => SaveAll());
        RefreshCommand = new RelayCommand(_ => Refresh());
        ApplyNowCommand = new RelayCommand(p => ApplyNow(p as GameProfile));
        ImportCommand = new RelayCommand(_ => Import());
        ExportCommand = new RelayCommand(_ => Export());

        _manager.ActiveProfileChanged += (_, p) =>
        {
            ActiveProfileName = p.Name;
            // Marshal to UI thread — TranslationEngine.StartAsync runs on
            // a worker, the resulting auto-create raises us off-thread.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                // Refresh the list when an auto-created profile shows up
                // (or anything else mutates the row) so the DataGrid
                // doesn't lag behind reality.
                if (!Profiles.Any(x => x.Id == p.Id))
                    Refresh();
            });
        };
        ActiveProfileName = _manager.ActiveProfile.Name;

        Refresh();
    }

    private string _activeProfileName = string.Empty;
    /// <summary>Name shown in the header — kept in sync via ProfileManager.ActiveProfileChanged.</summary>
    public string ActiveProfileName
    {
        get => _activeProfileName;
        private set => SetProperty(ref _activeProfileName, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand AddBlankCommand { get; }
    public ICommand CreateFromCurrentCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ApplyNowCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }

    public void Refresh()
    {
        Profiles.Clear();
        foreach (var p in _repo.GetAll())
            Profiles.Add(p);
        StatusMessage = LanguageManager.Format("Strings.Profiles.CountFmt", Profiles.Count);
    }

    private void AddBlank()
    {
        var p = new GameProfile
        {
            Name = LanguageManager.Get("Strings.Profiles.NewGameDefault"),
            MatchPattern = string.Empty,
        };
        _repo.Add(p);
        Profiles.Add(p);
        StatusMessage = LanguageManager.Get("Strings.Profiles.AddedHint");
    }

    private void CreateFromCurrent()
    {
        var p = _manager.CreateFromCurrentConfig(
            LanguageManager.Get("Strings.Profiles.NewFromCurrentDefault"),
            string.Empty,
            matchByProcess: false);
        Profiles.Add(p);
        StatusMessage = LanguageManager.Format("Strings.Profiles.CreatedFromCurrentFmt", p.Id);
    }

    private void Delete(GameProfile? profile)
    {
        if (profile == null) return;
        if (profile.IsDefault)
        {
            MessageBox.Show(
                LanguageManager.Get("Strings.Profiles.DefaultDeleteBody"),
                LanguageManager.Get("Strings.Profiles.DefaultDeleteTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                LanguageManager.Format("Strings.Profiles.DeleteBodyFmt", profile.Name),
                LanguageManager.Get("Strings.Profiles.DefaultDeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        if (_repo.Delete(profile.Id))
        {
            Profiles.Remove(profile);
            StatusMessage = LanguageManager.Get("Strings.Profiles.DeletedHint");
        }
    }

    private void SaveAll()
    {
        var ok = 0;
        foreach (var p in Profiles)
        {
            if (p.Id == 0) _repo.Add(p);
            else if (_repo.Update(p)) ok++;
        }
        StatusMessage = LanguageManager.Format("Strings.Profiles.SavedFmt", ok);
    }

    private void ApplyNow(GameProfile? profile)
    {
        if (profile == null) return;
        _manager.ApplyProfile(profile);
        StatusMessage = LanguageManager.Format("Strings.Profiles.AppliedFmt", profile.Name);
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Filter = LanguageManager.Get("Strings.Profiles.ImportFilter"),
            Title = LanguageManager.Get("Strings.Profiles.ImportTitle"),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<List<GameProfile>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null || imported.Count == 0)
            {
                StatusMessage = LanguageManager.Get("Strings.Profiles.ImportEmptyHint");
                return;
            }

            // Reset Id so SQLite assigns fresh ones; never overwrite the
            // Default profile during import (id=1 reserved).
            int n = 0;
            foreach (var p in imported)
            {
                p.Id = 0;
                if (_repo.Add(p) > 0) n++;
            }
            Refresh();
            StatusMessage = LanguageManager.Format("Strings.Profiles.ImportedFmt", n);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Profile import failed");
            MessageBox.Show(
                LanguageManager.Format("Strings.Profiles.ImportErrorFmt", ex.Message),
                LanguageManager.Get("Strings.Profiles.ImportErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = LanguageManager.Get("Strings.Profiles.ImportFilter"),
            FileName = $"profiles-export-{DateTime.Now:yyyy-MM-dd}.json",
            Title = LanguageManager.Get("Strings.Profiles.ExportTitle"),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // Skip Default during export — it's specific to the source
            // machine's current settings and shouldn't be portable.
            var toExport = Profiles.Where(p => !p.IsDefault).ToList();
            var json = JsonSerializer.Serialize(toExport,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusMessage = LanguageManager.Format("Strings.Profiles.ExportedFmt",
                toExport.Count, Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Profile export failed");
            MessageBox.Show(
                LanguageManager.Format("Strings.Profiles.ExportErrorFmt", ex.Message),
                LanguageManager.Get("Strings.Profiles.ExportErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
