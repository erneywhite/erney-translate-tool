using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ErneyTranslateTool.Models;

/// <summary>
/// One game-specific bundle of OCR/translation/overlay settings. When the
/// user picks a window in the Main tab, ProfileManager looks up a profile
/// whose <see cref="MatchPattern"/> matches the window title or process
/// name and applies all of its fields to the live <see cref="AppConfig"/>.
///
/// <para>
/// Implements INotifyPropertyChanged so DataGrid edits propagate (same
/// fix as <see cref="GlossaryEntry"/>).
/// </para>
/// </summary>
public class GameProfile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sentinel id of the always-present default profile, applied when no
    /// other profile matches the selected window. Stored as id=1 in the
    /// migration; later inserts get id=2+.
    /// </summary>
    public const long DefaultProfileId = 1;

    private long _id;
    private string _name = string.Empty;
    private string _matchPattern = string.Empty;
    private bool _matchByProcessName;

    private string _ocrEngine = "PaddleOCR";
    private string _sourceLanguage = string.Empty;
    private string _tesseractLanguage = "eng";
    private string _paddleLanguage = "en";

    private string _targetLanguage = "RU";
    private string _translationProvider = "MyMemory";

    private string _overlayFontFamily = "Segoe UI";
    private string _fontSizeMode = "Auto";
    private double _manualFontSize = 14;
    private double _overlayOpacity = 0.95;
    private string _backgroundColor = "#000000";
    private string _textColor = "#FFFFFF";
    private double _overlayCornerRadius = 4;

    private bool _glossaryEnabled = true;

    /// <summary>SQLite primary key.</summary>
    public long Id { get => _id; set => Set(ref _id, value); }

    /// <summary>Human-readable name shown in the Profiles tab and tray tooltip.</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// Substring (case-insensitive) matched against the window title — or
    /// the process name when <see cref="MatchByProcessName"/> is true.
    /// Empty pattern never matches anything (used by the Default profile,
    /// which is selected by fallback rather than match).
    /// </summary>
    public string MatchPattern { get => _matchPattern; set => Set(ref _matchPattern, value); }

    /// <summary>If true, <see cref="MatchPattern"/> is checked against the process name; otherwise against the window title.</summary>
    public bool MatchByProcessName { get => _matchByProcessName; set => Set(ref _matchByProcessName, value); }

    public string OcrEngine { get => _ocrEngine; set => Set(ref _ocrEngine, value); }
    public string SourceLanguage { get => _sourceLanguage; set => Set(ref _sourceLanguage, value); }
    public string TesseractLanguage { get => _tesseractLanguage; set => Set(ref _tesseractLanguage, value); }
    public string PaddleLanguage { get => _paddleLanguage; set => Set(ref _paddleLanguage, value); }

    public string TargetLanguage { get => _targetLanguage; set => Set(ref _targetLanguage, value); }
    public string TranslationProvider { get => _translationProvider; set => Set(ref _translationProvider, value); }

    public string OverlayFontFamily { get => _overlayFontFamily; set => Set(ref _overlayFontFamily, value); }
    public string FontSizeMode { get => _fontSizeMode; set => Set(ref _fontSizeMode, value); }
    public double ManualFontSize { get => _manualFontSize; set => Set(ref _manualFontSize, value); }
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public double OverlayCornerRadius { get => _overlayCornerRadius; set => Set(ref _overlayCornerRadius, value); }

    public bool GlossaryEnabled { get => _glossaryEnabled; set => Set(ref _glossaryEnabled, value); }

    /// <summary>True for the always-present "По умолчанию" profile (id=1) — UI uses this to lock the row from deletion.</summary>
    public bool IsDefault => Id == DefaultProfileId;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Id)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefault)));
    }
}
