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
    /// Selected window handle (HWND) for capture.
    /// </summary>
    public IntPtr TargetWindowHandle { get; set; } = IntPtr.Zero;

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
    /// OCR engine: "WindowsOcr" (built-in, system packs) or "Tesseract" (bundled).
    /// </summary>
    public string OcrEngine { get; set; } = "Tesseract";

    /// <summary>
    /// Tesseract language code (e.g. "eng", "rus", "jpn"). Multi-language allowed via "+".
    /// </summary>
    public string TesseractLanguage { get; set; } = "eng";

    /// <summary>
    /// When true, downloads of additional Tesseract languages pull from
    /// tessdata_best (4-5x larger but markedly better quality).
    /// </summary>
    public bool UseBestTessdata { get; set; } = true;
}
