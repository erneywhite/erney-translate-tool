using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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
        StatusMessage = $"Профилей: {Profiles.Count}";
    }

    private void AddBlank()
    {
        var p = new GameProfile
        {
            Name = "Новая игра",
            MatchPattern = string.Empty,
        };
        _repo.Add(p);
        Profiles.Add(p);
        StatusMessage = "Добавлен пустой профиль — заполни поля «Имя» и «Шаблон совпадения», затем «Сохранить».";
    }

    private void CreateFromCurrent()
    {
        var p = _manager.CreateFromCurrentConfig(
            "Новый из текущих настроек",
            string.Empty,
            matchByProcess: false);
        Profiles.Add(p);
        StatusMessage = $"Создан профиль из текущих настроек (id={p.Id}). Не забудь задать «Имя» и «Шаблон совпадения», затем «Сохранить».";
    }

    private void Delete(GameProfile? profile)
    {
        if (profile == null) return;
        if (profile.IsDefault)
        {
            MessageBox.Show("Профиль «По умолчанию» нельзя удалить — он используется как fallback.",
                "Удаление профиля", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Удалить профиль «{profile.Name}»?",
                "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        if (_repo.Delete(profile.Id))
        {
            Profiles.Remove(profile);
            StatusMessage = "Профиль удалён.";
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
        StatusMessage = $"Сохранено профилей: {ok}.";
    }

    private void ApplyNow(GameProfile? profile)
    {
        if (profile == null) return;
        _manager.ApplyProfile(profile);
        StatusMessage = $"Профиль «{profile.Name}» применён к текущим настройкам.";
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON-файл профилей (*.json)|*.json",
            Title = "Импорт профилей",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<List<GameProfile>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null || imported.Count == 0)
            {
                StatusMessage = "Файл пуст или не содержит профилей.";
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
            StatusMessage = $"Импортировано профилей: {n}.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Profile import failed");
            MessageBox.Show($"Не удалось импортировать: {ex.Message}",
                "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON-файл профилей (*.json)|*.json",
            FileName = $"profiles-export-{DateTime.Now:yyyy-MM-dd}.json",
            Title = "Экспорт профилей",
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
            StatusMessage = $"Экспортировано профилей: {toExport.Count} в {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Profile export failed");
            MessageBox.Show($"Не удалось экспортировать: {ex.Message}",
                "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
