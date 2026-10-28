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

    // v1.0.33: the pipeline calls ProcessFrame from a worker thread while
    // Reload (settings auto-save, profile switch) runs on the UI thread.
    // The lock only guards the backend REFERENCE and its lifetime, never an
    // OCR pass: a frame takes the current backend and runs without the lock,
    // so Reload swaps instantly instead of freezing the UI for a whole
    // inference. A backend replaced while a frame is still running on it is
    // disposed by that frame when it finishes (_retireInUse).
    private readonly object _backendLock = new();
    private IOcrBackend? _inUse;
    private bool _retireInUse;

    /// <summary>Drop a backend that's no longer current. Call under _backendLock.</summary>
    private void Retire(IOcrBackend? old)
    {
        if (old == null) return;
        old.StatusChanged -= OnBackendStatusChanged;
        if (ReferenceEquals(old, _inUse)) _retireInUse = true;
        else old.Dispose();
    }

    /// <summary>Rebuild the active backend from current settings.</summary>
    public void Reload()
    {
        lock (_backendLock)
            ReloadLocked();
        // Outside the lock: subscribers marshal to the UI thread.
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadLocked()
    {
        try
        {
            if (_disposed) return;
            Retire(_backend);
            _backend = null;

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

    public List<TranslationRegion> ProcessFrame(byte[] pngBytes)
    {
        IOcrBackend? backend;
        lock (_backendLock)
        {
            if (_disposed || _backend == null) return new List<TranslationRegion>();
            backend = _backend;
            _inUse = backend;
        }
        try
        {
            return backend.ProcessFrame(pngBytes);
        }
        finally
        {
            lock (_backendLock)
            {
                _inUse = null;
                if (_retireInUse)
                {
                    _retireInUse = false;
                    backend.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_backendLock)
        {
            if (_disposed) return;
            Retire(_backend);
            _backend = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
