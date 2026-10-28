using System;
using System.Windows;

namespace ErneyTranslateTool.Models;

/// <summary>
/// Represents a detected text region with translation data.
/// </summary>
public class TranslationRegion
{
    /// <summary>
    /// Unique identifier for this region.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Bounding box of the text region in screen coordinates.
    /// </summary>
    public Rect Bounds { get; set; }

    /// <summary>
    /// Original detected text.
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// Translated text (Russian by default).
    /// </summary>
    public string? TranslatedText { get; set; }

    /// <summary>
    /// Detected source language code.
    /// </summary>
    public string? SourceLanguage { get; set; }

    /// <summary>
    /// Hash of the region image for change detection.
    /// </summary>
    public string ImageHash { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when this region was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this region was served from cache.
    /// </summary>
    public bool IsFromCache { get; set; }

    /// <summary>
    /// Whether the text contains Cyrillic characters (skip translation).
    /// </summary>
    public bool ContainsCyrillic { get; set; }

    /// <summary>v1.0.35: colour behind the text in the game frame (see ColorSampler); null if not sampled.</summary>
    public System.Windows.Media.Color? SampledBackground { get; set; }

    /// <summary>v1.0.35: colour of the letters in the game frame; null when they don't stand out.</summary>
    public System.Windows.Media.Color? SampledText { get; set; }

    /// <summary>v1.0.35: height of one source line in pixels — caps the font size in "replace text" mode.</summary>
    public double SourceLineHeight { get; set; }

    /// <summary>v1.0.39: share of the box covered by letters of <see cref="SampledText"/>'s colour when it was read.</summary>
    public double TextPixelShare { get; set; }
}
