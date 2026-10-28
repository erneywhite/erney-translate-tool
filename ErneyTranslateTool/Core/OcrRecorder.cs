using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Diagnostic recording of raw OCR output, one JSON line per frame that
/// reached OCR (v1.0.35). A recording of a scene that flickers is replayed
/// through FrameLayout in the tests, so a grouping change can be checked in
/// seconds instead of by watching the game. Frames skipped by the pixel hash
/// are not written: they produce no work in the engine either.
/// </summary>
public sealed class OcrRecorder
{
    private readonly ILogger _logger;
    private readonly string _directory;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private Stopwatch _clock = new();
    private int _frames;

    public OcrRecorder(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    /// <summary>Path of the file being written, or null when not recording.</summary>
    public string? CurrentFile { get; private set; }

    public bool IsRecording
    {
        get { lock (_lock) return _writer != null; }
    }

    /// <summary>Open a new recording. Does nothing if one is already open.</summary>
    public void Start(string windowTitle, string ocrEngine, string ocrLanguage)
    {
        lock (_lock)
        {
            if (_writer != null) return;
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                var name = $"ocr-{DateTime.Now:yyyyMMdd-HHmmss}-{SafeName(windowTitle)}.jsonl";
                var path = Path.Combine(_directory, name);
                _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                CurrentFile = path;
                _clock = Stopwatch.StartNew();
                _frames = 0;
                _writer.WriteLine(JsonSerializer.Serialize(new RecordingHeader
                {
                    Version = 1,
                    Started = DateTime.Now.ToString("O"),
                    Window = windowTitle,
                    OcrEngine = ocrEngine,
                    OcrLanguage = ocrLanguage,
                }, Json));
                _logger.Information("OCR recording started: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OCR recording could not start");
                _writer?.Dispose();
                _writer = null;
                CurrentFile = null;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_writer == null) return;
            _writer.Dispose();
            _writer = null;
            _logger.Information("OCR recording stopped: {Frames} frames in {Path}", _frames, CurrentFile);
            CurrentFile = null;
        }
    }

    /// <summary>Write one frame. Empty <paramref name="regions"/> is a real frame too (text vanished).</summary>
    public void Record(IReadOnlyList<TranslationRegion> regions, int frameWidth, int frameHeight)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new RecordedFrame
                {
                    TimeMs = _clock.ElapsedMilliseconds,
                    Width = frameWidth,
                    Height = frameHeight,
                    Regions = regions.Select(r => new RecordedRegion
                    {
                        // OCR boxes are whole pixels scaled by DPI; a tenth is plenty.
                        X = Math.Round(r.Bounds.X, 1), Y = Math.Round(r.Bounds.Y, 1),
                        W = Math.Round(r.Bounds.Width, 1), H = Math.Round(r.Bounds.Height, 1),
                        Text = r.OriginalText,
                    }).ToList(),
                }, Json));
                _frames++;
            }
            catch (Exception ex)
            {
                // Disk full or the file was removed: stop instead of failing every frame.
                _logger.Warning(ex, "OCR recording write failed, stopping");
                _writer.Dispose();
                _writer = null;
                CurrentFile = null;
            }
        }
    }

    /// <summary>Read a recording back: the OCR regions of each frame, in order.</summary>
    public static List<List<TranslationRegion>> Load(string path)
    {
        var frames = new List<List<TranslationRegion>>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("regions", out _)) continue; // header
            var frame = doc.RootElement.Deserialize<RecordedFrame>(Json)!;
            frames.Add(frame.Regions.Select(r => new TranslationRegion
            {
                Bounds = new Rect(r.X, r.Y, r.W, r.H),
                OriginalText = r.Text,
            }).ToList());
        }
        return frames;
    }

    private static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        if (cleaned.Length > 40) cleaned = cleaned.Substring(0, 40);
        return cleaned.Length == 0 ? "window" : cleaned;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private sealed class RecordingHeader
    {
        public int Version { get; set; }
        public string Started { get; set; } = "";
        public string Window { get; set; } = "";
        public string OcrEngine { get; set; } = "";
        public string OcrLanguage { get; set; } = "";
    }

    private sealed class RecordedFrame
    {
        [JsonPropertyName("t")] public long TimeMs { get; set; }
        [JsonPropertyName("w")] public int Width { get; set; }
        [JsonPropertyName("h")] public int Height { get; set; }
        public List<RecordedRegion> Regions { get; set; } = new();
    }

    private sealed class RecordedRegion
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Text { get; set; } = "";
    }
}
