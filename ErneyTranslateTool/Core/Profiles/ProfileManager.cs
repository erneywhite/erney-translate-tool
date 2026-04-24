using System;
using System.Linq;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core.Profiles;

/// <summary>
/// Owns the GameProfile concept end-to-end:
/// <list type="bullet">
///   <item>Migrates legacy AppConfig fields into the "Default" profile on first run.</item>
///   <item>Picks the right profile when the user selects a window (substring match on title or process name).</item>
///   <item>Copies a profile's fields into the live AppConfig and saves it,
///         so the rest of the codebase keeps reading from a single source.</item>
/// </list>
/// </summary>
public class ProfileManager
{
    private readonly GameProfileRepository _repo;
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private GameProfile? _activeProfile;

    /// <summary>Profile currently driving the live AppConfig — never null after construction.</summary>
    public GameProfile ActiveProfile => _activeProfile ?? throw new InvalidOperationException(
        "ProfileManager not initialised — Default profile missing");

    /// <summary>Raised after <see cref="ApplyProfile"/> mutates AppConfig — UI uses this to refresh.</summary>
    public event EventHandler<GameProfile>? ActiveProfileChanged;

    public ProfileManager(GameProfileRepository repo, AppSettings settings, ILogger logger)
    {
        _repo = repo;
        _settings = settings;
        _logger = logger;

        EnsureDefaultProfile();
        _activeProfile = _repo.GetById(GameProfile.DefaultProfileId)
            ?? new GameProfile { Id = GameProfile.DefaultProfileId, Name = "По умолчанию" };
    }

    /// <summary>
    /// First-run migration: if no profiles exist, snapshot the current
    /// AppConfig into a Default profile (id=1) so we don't lose anything
    /// the user already configured before v1.0.6.
    /// </summary>
    private void EnsureDefaultProfile()
    {
        if (_repo.Count() > 0) return;

        var c = _settings.Config;
        var def = new GameProfile
        {
            Id = GameProfile.DefaultProfileId,
            Name = "По умолчанию",
            MatchPattern = string.Empty,           // never matches; used as fallback
            MatchByProcessName = false,

            OcrEngine = string.IsNullOrWhiteSpace(c.OcrEngine) ? "PaddleOCR" : c.OcrEngine,
            SourceLanguage = c.SourceLanguage ?? string.Empty,
            TesseractLanguage = string.IsNullOrWhiteSpace(c.TesseractLanguage) ? "eng" : c.TesseractLanguage,
            PaddleLanguage = string.IsNullOrWhiteSpace(c.PaddleLanguage) ? "en" : c.PaddleLanguage,

            TargetLanguage = string.IsNullOrWhiteSpace(c.TargetLanguage) ? "RU" : c.TargetLanguage,
            TranslationProvider = string.IsNullOrWhiteSpace(c.TranslationProvider) ? "MyMemory" : c.TranslationProvider,

            OverlayFontFamily = c.OverlayFontFamily,
            FontSizeMode = c.FontSizeMode,
            ManualFontSize = c.ManualFontSize,
            OverlayOpacity = c.OverlayOpacity,
            BackgroundColor = c.BackgroundColor,
            TextColor = c.TextColor,
            OverlayCornerRadius = c.OverlayCornerRadius,

            GlossaryEnabled = c.GlossaryEnabled,
        };
        _repo.Add(def);
        _logger.Information("Profiles: seeded Default profile from existing AppConfig");
    }

    /// <summary>
    /// Find the best profile for a given window. Default is returned when
    /// no user-defined profile matches — never returns null.
    /// </summary>
    public GameProfile FindForWindow(string windowTitle, string processName)
    {
        var all = _repo.GetAll();

        // Skip Default in the match scan — it's the fallback, not a real
        // pattern. We also skip empty patterns so a user-created profile
        // with a blank match field doesn't collide with everything.
        foreach (var p in all)
        {
            if (p.IsDefault) continue;
            if (string.IsNullOrWhiteSpace(p.MatchPattern)) continue;

            var haystack = p.MatchByProcessName ? processName : windowTitle;
            if (haystack != null &&
                haystack.IndexOf(p.MatchPattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return p;
            }
        }
        return all.FirstOrDefault(p => p.IsDefault)
            ?? throw new InvalidOperationException("Default profile missing — DB corrupted");
    }

    /// <summary>
    /// Like <see cref="FindForWindow"/>, but if no user profile matches and
    /// we have a usable process name, auto-create one based on the current
    /// Default settings and key it on the process name. This is what gives
    /// the user the "settings remembered per-game automatically" experience
    /// — first launch on Witcher3.exe creates a "Witcher3" profile, every
    /// future launch picks it up by process name without any UI work.
    /// </summary>
    public GameProfile GetOrCreateForWindow(string windowTitle, string processName)
    {
        var existing = FindForWindow(windowTitle, processName);
        if (!existing.IsDefault) return existing;

        // Nothing matched. Decide whether to mint a new profile:
        //   - process name must be present and not the placeholder we use
        //     when GetProcessById fails (see WindowPickerService);
        //   - keep names short and recognisable.
        var pname = (processName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(pname) ||
            string.Equals(pname, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        // Snapshot current Default/active fields so the new profile starts
        // identical — the user's edits while it's active will be persisted
        // into IT (not Default) by SaveActiveProfileFromCurrentConfig.
        var auto = CreateFromCurrentConfig(
            name: pname,
            matchPattern: pname,
            matchByProcess: true);
        _logger.Information("Profiles: auto-created '{Name}' for {Process}", auto.Name, pname);
        return auto;
    }

    /// <summary>
    /// Persist the live AppConfig fields back into the active profile. Called
    /// from SettingsViewModel.Save so any tweak the user makes to the
    /// translation-settings or overlay-settings tabs while a profile is
    /// active sticks to that profile (rather than only living in
    /// settings.json).
    /// </summary>
    public void SaveActiveProfileFromCurrentConfig()
    {
        if (_activeProfile == null) return;
        var p = _activeProfile;
        var c = _settings.Config;

        p.OcrEngine = c.OcrEngine;
        p.SourceLanguage = c.SourceLanguage;
        p.TesseractLanguage = c.TesseractLanguage;
        p.PaddleLanguage = c.PaddleLanguage;

        p.TargetLanguage = c.TargetLanguage;
        p.TranslationProvider = c.TranslationProvider;

        p.OverlayFontFamily = c.OverlayFontFamily;
        p.FontSizeMode = c.FontSizeMode;
        p.ManualFontSize = c.ManualFontSize;
        p.OverlayOpacity = c.OverlayOpacity;
        p.BackgroundColor = c.BackgroundColor;
        p.TextColor = c.TextColor;
        p.OverlayCornerRadius = c.OverlayCornerRadius;

        p.GlossaryEnabled = c.GlossaryEnabled;

        if (_repo.Update(p))
        {
            _logger.Debug("Profiles: saved active profile '{Name}' (Id={Id})", p.Name, p.Id);
            // Re-raise so the Profiles UI refreshes the row in-place.
            ActiveProfileChanged?.Invoke(this, p);
        }
    }

    /// <summary>
    /// Copy the profile's fields into the live AppConfig, save settings,
    /// and notify subscribers so dependent services (TranslationService,
    /// OcrService, OverlayManager) can pick up the new values.
    /// </summary>
    public void ApplyProfile(GameProfile p)
    {
        var c = _settings.Config;
        c.OcrEngine = p.OcrEngine;
        c.SourceLanguage = p.SourceLanguage;
        c.TesseractLanguage = p.TesseractLanguage;
        c.PaddleLanguage = p.PaddleLanguage;

        c.TargetLanguage = p.TargetLanguage;
        c.TranslationProvider = p.TranslationProvider;

        c.OverlayFontFamily = p.OverlayFontFamily;
        c.FontSizeMode = p.FontSizeMode;
        c.ManualFontSize = p.ManualFontSize;
        c.OverlayOpacity = p.OverlayOpacity;
        c.BackgroundColor = p.BackgroundColor;
        c.TextColor = p.TextColor;
        c.OverlayCornerRadius = p.OverlayCornerRadius;

        c.GlossaryEnabled = p.GlossaryEnabled;

        _settings.Save();

        _activeProfile = p;
        _logger.Information("Profiles: applied '{Name}' (Id={Id})", p.Name, p.Id);
        ActiveProfileChanged?.Invoke(this, p);
    }

    /// <summary>
    /// Snapshot the current AppConfig into a new profile — used by the
    /// "Создать профиль из текущих настроек" button in the Profiles UI.
    /// </summary>
    public GameProfile CreateFromCurrentConfig(string name, string matchPattern, bool matchByProcess)
    {
        var c = _settings.Config;
        var p = new GameProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Новый профиль" : name,
            MatchPattern = matchPattern ?? string.Empty,
            MatchByProcessName = matchByProcess,

            OcrEngine = c.OcrEngine,
            SourceLanguage = c.SourceLanguage,
            TesseractLanguage = c.TesseractLanguage,
            PaddleLanguage = c.PaddleLanguage,

            TargetLanguage = c.TargetLanguage,
            TranslationProvider = c.TranslationProvider,

            OverlayFontFamily = c.OverlayFontFamily,
            FontSizeMode = c.FontSizeMode,
            ManualFontSize = c.ManualFontSize,
            OverlayOpacity = c.OverlayOpacity,
            BackgroundColor = c.BackgroundColor,
            TextColor = c.TextColor,
            OverlayCornerRadius = c.OverlayCornerRadius,

            GlossaryEnabled = c.GlossaryEnabled,
        };
        _repo.Add(p);
        return p;
    }
}
