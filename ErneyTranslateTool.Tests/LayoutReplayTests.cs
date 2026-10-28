using System.IO;
using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Models;
using Serilog;
using Xunit.Abstractions;

namespace ErneyTranslateTool.Tests;

public class LayoutReplayTests
{
    private readonly ITestOutputHelper _out;

    public LayoutReplayTests(ITestOutputHelper output) => _out = output;

    private static TranslationRegion Line(double x, double y, double w, double h, string text) =>
        new() { Bounds = new Rect(x, y, w, h), OriginalText = text };

    [Fact]
    public void CountFlicker_CountsReturnsNotSceneChanges()
    {
        Assert.Equal(0, LayoutReplay.CountFlicker(new[] { "a", "a", "b", "b", "c" }));
        Assert.Equal(1, LayoutReplay.CountFlicker(new[] { "a", "b", "a" }));
        Assert.Equal(2, LayoutReplay.CountFlicker(new[] { "a", "b", "a", "b" }));
        // Hidden in between is the text vanishing, not a layout flip.
        Assert.Equal(0, LayoutReplay.CountFlicker(new string?[] { "a", null, "a" }));
        // Too far back to be flicker: the scene came back.
        Assert.Equal(0, LayoutReplay.CountFlicker(new[] { "a", "b", "b", "b", "a" }, window: 3));
    }

    [Fact]
    public void StaticSceneWithJitter_KeepsOneLayout()
    {
        // Two-line dialogue, OCR wobbling by a pixel and misreading a letter now and then.
        var frames = new List<List<TranslationRegion>>();
        for (int i = 0; i < 30; i++)
        {
            var d = i % 3 - 1;
            frames.Add(new()
            {
                Line(100 + d, 400, 600, 28 + d, i % 7 == 3 ? "Welcome to the Yankee Massage parlor," : "Welcome to the Yankee Massage parlour,"),
                Line(100, 432 - d, 520, 28, "how can I help you today?"),
            });
        }

        var replay = LayoutReplay.Run(frames);
        _out.WriteLine(replay.Describe());
        Assert.Equal(0, LayoutReplay.CountFlicker(replay.Layouts));
    }

    [Fact]
    public void Recorder_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ett-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rec = new OcrRecorder(dir, new LoggerConfiguration().CreateLogger());
            rec.Start("Game: \"test\" / 1", "PaddleOCR", "en");
            rec.Record(new[] { Line(1.5, 2, 3, 4, "Привет \"мир\"") }, 800, 600);
            rec.Record(Array.Empty<TranslationRegion>(), 800, 600);
            var file = rec.CurrentFile!;
            rec.Stop();
            Assert.False(rec.IsRecording);

            var frames = OcrRecorder.Load(file);
            Assert.Equal(2, frames.Count);
            Assert.Equal("Привет \"мир\"", frames[0][0].OriginalText);
            Assert.Equal(new Rect(1.5, 2, 3, 4), frames[0][0].Bounds);
            Assert.Empty(frames[1]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Every recording dropped into Recordings/ (from Program → Diagnostics)
    /// must replay without layout flicker. A new recording of a flickering
    /// scene makes this fail until the grouping is fixed.
    /// </summary>
    [Fact]
    public void RecordedScenes_DoNotFlicker()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Recordings");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl") : Array.Empty<string>();
        var failed = new List<string>();
        foreach (var file in files)
        {
            var replay = LayoutReplay.Run(OcrRecorder.Load(file));
            var flicker = LayoutReplay.FlickerFrames(replay.Layouts);
            _out.WriteLine($"{Path.GetFileName(file)}: {replay.Layouts.Count} frames, flicker {flicker.Count}" +
                           (flicker.Count > 0 ? $" at #{string.Join(", #", flicker.Take(20))}" : ""));
            if (flicker.Count > 0)
            {
                _out.WriteLine(replay.Describe());
                failed.Add(Path.GetFileName(file));
            }
        }
        Assert.Empty(failed);
    }
}
