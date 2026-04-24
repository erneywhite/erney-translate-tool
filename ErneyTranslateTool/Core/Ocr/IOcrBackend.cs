using System;
using System.Collections.Generic;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// Pluggable OCR engine. Each backend reports its own list of available
/// languages because Windows OCR uses system packs, while Tesseract uses
/// .traineddata files in a folder.
/// </summary>
public enum OcrBackendState
{
    NotInitialized,
    Loading,
    Ready,
    Failed,
}

public interface IOcrBackend : IDisposable
{
    string Name { get; }
    string CurrentLanguageTag { get; }

    /// <summary>Languages this backend can use right now.</summary>
    List<(string Tag, string DisplayName)> GetAvailableLanguages();

    /// <summary>Try to switch to the given language. Returns true on success.</summary>
    bool SetLanguage(string tag);

    /// <summary>Run OCR on a PNG-encoded image and return detected text regions.</summary>
    List<TranslationRegion> ProcessFrame(byte[] pngBytes);

    /// <summary>Current high-level state for UI indicators.</summary>
    OcrBackendState State { get; }

    /// <summary>Human-readable status message ("Готов", "Скачивание модели en...", etc.).</summary>
    string StatusMessage { get; }

    /// <summary>Fired when State or StatusMessage changes.</summary>
    event EventHandler? StatusChanged;
}
