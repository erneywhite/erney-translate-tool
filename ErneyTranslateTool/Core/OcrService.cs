using System;
using System.Collections.Generic;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Facade over a swappable OCR backend (Windows.Media.OCR or Tesseract).
/// Picks the engine based on AppConfig.OcrEngine and exposes the same
/// interface to the rest of the app, so callers don't need to care which
/// backend is active.
/// </summary>
public class OcrService : IDisposable
{
    public const string EngineWindows = "WindowsOcr";
    public const string EngineTesseract = "Tesseract";
    public const string EnginePaddle = "PaddleOCR";

    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly TessdataManager _tessdata;
    private IOcrBackend? _backend;
    private bool _disposed;

    public OcrService(ILogger logger, AppSettings settings, TessdataManager tessdata)
    {
        _logger = logger;
        _settings = settings;
        _tessdata = tessdata;
        Reload();
    }

    public string CurrentEngine => _backend?.Name ?? "(none)";
    public string CurrentLanguageTag => _backend?.CurrentLanguageTag ?? string.Empty;

    public OcrBackendState State => _backend?.State ?? OcrBackendState.NotInitialized;
    public string StatusMessage => _backend?.StatusMessage ?? "Не инициализирован";

    /// <summary>Fires whenever the active backend's state or status changes,
    /// or when Reload() swaps in a new backend.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Rebuild the active backend from current settings.</summary>
    public void Reload()
    {
        try
        {
            if (_backend != null) _backend.StatusChanged -= OnBackendStatusChanged;
            _backend?.Dispose();

            var engine = _settings.Config.OcrEngine;
            if (string.IsNullOrWhiteSpace(engine)) engine = EnginePaddle;

            if (engine == EngineWindows)
            {
                _backend = new WindowsOcrBackend(_logger, _settings.Config.SourceLanguage);
            }
            else if (engine == EnginePaddle)
            {
                _backend = new PaddleOcrBackend(_logger,
                    string.IsNullOrWhiteSpace(_settings.Config.PaddleLanguage)
                        ? "en"
                        : _settings.Config.PaddleLanguage);
            }
            else
            {
                _backend = new TesseractOcrBackend(_tessdata, _logger,
                    string.IsNullOrWhiteSpace(_settings.Config.TesseractLanguage)
                        ? "eng"
                        : _settings.Config.TesseractLanguage);
            }

            _backend.StatusChanged += OnBackendStatusChanged;
            _logger.Information("OCR backend active: {Name} / {Lang}",
                _backend.Name, _backend.CurrentLanguageTag);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load OCR backend");
        }
    }

    private void OnBackendStatusChanged(object? sender, EventArgs e) =>
        StatusChanged?.Invoke(this, EventArgs.Empty);

    public List<(string Tag, string DisplayName)> GetAvailableLanguages() =>
        _backend?.GetAvailableLanguages() ?? new List<(string, string)>();

    public bool SetLanguage(string tag) => _backend?.SetLanguage(tag) ?? false;

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes) =>
        _backend?.ProcessFrame(pngBytes) ?? new List<TranslationRegion>();

    public void Dispose()
    {
        if (_disposed) return;
        _backend?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
