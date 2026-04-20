using System;

namespace ErneyTranslateTool.Models;

/// <summary>
/// Represents a translation session for history tracking.
/// </summary>
public class SessionHistory
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Session start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Target window title (game name).
    /// </summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>
    /// Total characters translated in this session.
    /// </summary>
    public int TotalCharacters { get; set; }

    /// <summary>
    /// Total translations performed.
    /// </summary>
    public int TotalTranslations { get; set; }
}

/// <summary>
/// Represents a single translation entry in history.
/// </summary>
public class TranslationEntry
{
    /// <summary>
    /// Unique entry identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to session.
    /// </summary>
    public int SessionId { get; set; }

    /// <summary>
    /// Timestamp of translation.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Original source text.
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// Translated text.
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>
    /// Source language code.
    /// </summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Whether translation was from cache.
    /// </summary>
    public bool IsFromCache { get; set; }
}
