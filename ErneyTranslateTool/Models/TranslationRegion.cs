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
}
