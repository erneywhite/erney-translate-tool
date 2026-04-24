using System;
using System.Windows.Media;

namespace ErneyTranslateTool.Models;

/// <summary>
/// Application configuration model for settings persistence.
/// </summary>
public class AppConfig
{
    /// <summary>
    /// Encrypted DeepL API key.
    /// </summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// Target translation language code (e.g., "RU", "EN", "JA").
    /// </summary>
    public string TargetLanguage { get; set; } = "RU";

    /// <summary>
    /// Selected overlay font family name.
    /// </summary>
    public string OverlayFontFamily { get; set; } = "Segoe UI";

    /// <summary>
    /// Overlay font size mode: "Auto" or "Manual".
    /// </summary>
    public string FontSizeMode { get; set; } = "Auto";

    /// <summary>
    /// Manual font size in points (8-32).
    /// </summary>
    public double ManualFontSize { get; set; } = 14;

    /// <summary>
    /// Overlay background opacity (0.6 - 1.0). Near-opaque by default so the
    /// translation actually hides the original text underneath.
    /// </summary>
    public double OverlayOpacity { get; set; } = 0.95;

    /// <summary>
    /// Overlay background color (hex format).
    /// </summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>
    /// Overlay text color (hex format).
    /// </summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>
    /// Border corner radius for overlay rectangles, in DIPs (0 = sharp corners).
    /// </summary>
    public double OverlayCornerRadius { get; set; } = 4;

    /// <summary>
    /// Application theme id ("Dark", "Light", "Nord"). See ThemeManager.
    /// </summary>
    public string AppTheme { get; set; } = "Dark";

    /// <summary>When true, the [×] button minimises to tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>One-shot flag: have we shown the "minimised to tray" balloon
    /// at least once? Set to true after the first close so we don't nag.</summary>
    public bool CloseToTrayBalloonShown { get; set; } = false;

    /// <summary>When true, the app pings GitHub Releases on startup to look
    /// for a newer version. Failed checks are silent.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Version string the user has already been shown the "what's new" dialog
    /// for. On startup we compare this against the running assembly version —
    /// if they differ (and the stored value isn't empty), we know the user
    /// just upgraded and we surface the release notes once. Empty for first
    /// install (no need to show notes when there's no prior version).
    /// </summary>
    public string LastSeenReleaseVersion { get; set; } = string.Empty;

    /// <summary>
    /// Selected window handle (HWND) for capture, stored as long because
    /// System.Text.Json refuses to serialize IntPtr. Round-trip via the
    /// helper property below.
    /// </summary>
    public long TargetWindowHandleValue { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IntPtr TargetWindowHandle
    {
        get => new IntPtr(TargetWindowHandleValue);
        set => TargetWindowHandleValue = value.ToInt64();
    }

    /// <summary>
    /// Selected window title for display.
    /// </summary>
    public string TargetWindowTitle { get; set; } = string.Empty;

    /// <summary>
    /// Whether translation is currently enabled.
    /// </summary>
    public bool TranslationEnabled { get; set; } = false;

    /// <summary>
    /// Whether overlay is currently visible.
    /// </summary>
    public bool OverlayVisible { get; set; } = true;

    /// <summary>
    /// Whether to show onboarding wizard on next startup.
    /// </summary>
    public bool ShowOnboarding { get; set; } = true;

    /// <summary>
    /// Statistics: total characters translated today.
    /// </summary>
    public int CharactersTranslatedToday { get; set; } = 0;

    /// <summary>
    /// Statistics: date when characters count was reset.
    /// </summary>
    public DateTime CharactersResetDate { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Maximum size of the SQLite translation cache in megabytes. When the
    /// file grows past this, oldest-accessed entries are evicted (LRU) until
    /// it's back under ~90% of the limit. Set to 0 for unlimited.
    /// Default 200 MB matches a typical visual-novel save after several
    /// hundred hours.
    /// </summary>
    public int MaxCacheSizeMb { get; set; } = 200;

    /// <summary>
    /// Statistics: total cache hits.
    /// </summary>
    public int CacheHits { get; set; } = 0;

    /// <summary>
    /// Statistics: total cache misses.
    /// </summary>
    public int CacheMisses { get; set; } = 0;

    /// <summary>
    /// Hotkey for toggling translation on/off (default "Ctrl+Shift+T").
    /// </summary>
    public string ToggleTranslationHotkey { get; set; } = "Ctrl+Shift+T";

    /// <summary>
    /// Hotkey for toggling overlay visibility (default "Ctrl+Shift+H").
    /// </summary>
    public string ToggleOverlayHotkey { get; set; } = "Ctrl+Shift+H";

    /// <summary>
    /// Source OCR language tag (e.g. "en-US"). Empty = auto.
    /// </summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Translation provider id: DeepL / MyMemory / GoogleFree / LibreTranslate.
    /// </summary>
    public string TranslationProvider { get; set; } = "MyMemory";

    /// <summary>
    /// Optional email for MyMemory (raises daily limit from 5K to 50K chars).
    /// </summary>
    public string MyMemoryEmail { get; set; } = string.Empty;

    /// <summary>
    /// LibreTranslate instance URL (defaults to public site).
    /// </summary>
    public string LibreTranslateUrl { get; set; } = "https://libretranslate.com";

    /// <summary>
    /// Optional API key for LibreTranslate instances that require one.
    /// </summary>
    public string LibreTranslateApiKey { get; set; } = string.Empty;

    /// <summary>
    /// OCR engine: "PaddleOCR" (default — neural, best accuracy on stylized
    /// fonts), "Tesseract" (bundled, faster), or "WindowsOcr" (built-in but
    /// requires system language packs). PaddleOCR downloads its model on
    /// first use of each language.
    /// </summary>
    public string OcrEngine { get; set; } = "PaddleOCR";

    /// <summary>
    /// Tesseract language code (e.g. "eng", "rus", "jpn"). Multi-language allowed via "+".
    /// </summary>
    public string TesseractLanguage { get; set; } = "eng";

    /// <summary>
    /// When true, downloads of additional Tesseract languages pull from
    /// tessdata_best (4-5x larger but markedly better quality).
    /// </summary>
    public bool UseBestTessdata { get; set; } = true;

    /// <summary>
    /// PaddleOCR language family ("en", "ja", "zh", "ko"). PaddleOCR uses
    /// short codes mapped to its bundled model families, not Tesseract's
    /// 3-letter codes — kept as a separate field for clarity.
    /// </summary>
    public string PaddleLanguage { get; set; } = "en";

    /// <summary>
    /// Master kill-switch for the proper-noun glossary. When false, no
    /// post-translation replacement happens at all — useful as a quick
    /// "did the glossary just break my translations?" check without
    /// having to delete rules.
    /// </summary>
    public bool GlossaryEnabled { get; set; } = true;
}
