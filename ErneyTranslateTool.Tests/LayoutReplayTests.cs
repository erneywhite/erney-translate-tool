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

    [Theory]
    [InlineData("*", true)]
    [InlineData("$3", true)]
    [InlineData("*3", true)]
    [InlineData("83", true)]
    [InlineData("C", true)]
    [InlineData("x($)", true)]
    [InlineData("-", true)]
    [InlineData("SE", false)]
    [InlineData("BGM", false)]
    [InlineData("ver.1.1.8", false)]
    [InlineData("< ENGLISH", false)]
    [InlineData("Да", false)]
    [InlineData("猫", false)]      // one CJK character is a word
    [InlineData("I am", false)]
    public void Noise_IsTextWithoutTwoLettersInARow(string text, bool noise) =>
        Assert.Equal(noise, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.LooksLikeNoise(text));

    [Fact]
    public void HoldBriefDropouts_KeepsOneMissingBlock_ButNotAWholeOldScene()
    {
        var layout = new FrameLayout(new LoggerConfiguration().CreateLogger());
        var menu = new List<TranslationRegion>
        {
            Line(100, 100, 100, 30, "NORMAL"), Line(100, 150, 100, 30, "HARD"),
            Line(100, 200, 100, 30, "S.HARD"), Line(300, 500, 40, 30, "SE"),
        };
        layout.HoldBriefDropouts(menu, 2);

        // OCR missed "SE" this frame: it stays.
        var held = layout.HoldBriefDropouts(menu.Take(3).ToList(), 2);
        Assert.Contains(held, r => r.OriginalText == "SE");

        // A new scene elsewhere on screen: the old menu goes at once.
        var dialogue = new List<TranslationRegion> { Line(400, 800, 900, 40, "Hmm... I prefer pink ones.") };
        Assert.Single(layout.HoldBriefDropouts(dialogue, 2));
    }

    /// <summary>
    /// A HUD whose labels change in place: grouping inherited from an earlier
    /// frame by position alone glued a label to a hint across the screen
    /// (blocks 175 and 406 px tall). The real blocks there are under 70 px.
    /// </summary>
    [Fact]
    public void InheritedGrouping_DoesNotGlueUnrelatedText()
    {
        var frames = OcrRecorder.Load(Path.Combine(AppContext.BaseDirectory, "Recordings", "vn-minigame-hud.jsonl"));
        var layout = new FrameLayout(new LoggerConfiguration().CreateLogger());
        foreach (var recorded in frames)
        {
            var raw = recorded.ToList();
            ErneyTranslateTool.Core.Ocr.OcrTextHelpers.DropNoise(raw);
            if (raw.Count == 0 || layout.TryReusePreviousFrame(raw, FrameLayout.BlockVanishFrames, out _)) continue;
            var blocks = layout.ApplyGroupingHysteresisOrGroup(raw);
            Assert.All(blocks, b => Assert.True(b.Bounds.Height < 200, $"{b.Bounds.Height:F0} px: '{b.OriginalText}'"));
            foreach (var b in blocks) b.TranslatedText = b.OriginalText;
            layout.RememberCompletedFrame(raw, blocks);
        }
    }

    [Fact]
    public void HoldBriefDropouts_DoesNotHoldABlockWhoseTextIsGone()
    {
        var layout = new FrameLayout(new LoggerConfiguration().CreateLogger());
        var lines = new List<TranslationRegion>
        {
            Line(100, 100, 100, 30, "NORMAL"), Line(100, 150, 100, 30, "HARD"),
            Line(100, 200, 100, 30, "S.HARD"), Line(300, 500, 40, 30, "SE"),
        };
        layout.HoldBriefDropouts(lines, 3);
        var held = layout.HoldBriefDropouts(lines.Take(3).ToList(), 3, isBlank: r => r.Bounds.Top >= 500);
        Assert.DoesNotContain(held, r => r.OriginalText == "SE");
    }

    [Fact]
    public void TitleScreenLogo_IsLeftAlone()
    {
        // Kon's Lesson title screen, as OCR saw it.
        var lines = new List<TranslationRegion>
        {
            Line(999, 85, 146, 85, "妖狐"), Line(1146, 153, 85, 17, "Kon's Lesson!"), Line(1011, 169, 227, 59, "戀愛學程"),
            Line(1021, 279, 74, 28, "新遊戲"), Line(1021, 342, 47, 29, "載入"), Line(1021, 406, 50, 29, "設定"),
            Line(1019, 469, 52, 29, "相冊"), Line(1019, 533, 52, 27, "退出"),
        };
        Assert.Equal(2, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.DropOversized(lines));
        Assert.Equal(new[] { "Kon's Lesson!", "新遊戲", "載入", "設定", "相冊", "退出" }, lines.Select(l => l.OriginalText));
    }

    [Fact]
    public void TwoLineLogo_GoesWhole_ButALoneBigSignStays()
    {
        // Night Adventure title screen.
        var night = new List<TranslationRegion>
        {
            Line(228, 37, 163, 51, "HIGHT"), Line(151, 102, 322, 43, "ROENTUFE"),
            Line(253, 408, 92, 22, "Continue"), Line(243, 462, 112, 23, "New Game"), Line(262, 513, 74, 28, "Gallery"),
            Line(256, 569, 88, 26, "Settings"), Line(278, 622, 41, 25, "Exit"),
        };
        Assert.Equal(2, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.DropOversized(night));

        // Yankee Massage: the START MSG clipboard is 1.7x the median but stands alone.
        var yankee = new List<TranslationRegion>
        {
            Line(152, 77, 92, 25, "NORMAL"), Line(155, 109, 88, 25, "HARD"), Line(154, 142, 92, 26, "S.HARD"),
            Line(72, 470, 175, 36, "< ENGLISH"), Line(103, 541, 54, 35, "BGM"), Line(263, 542, 36, 32, "SE"),
            Line(737, 710, 94, 30, "ver.1.1.8"), Line(728, 751, 102, 24, "Visit Warumiyo"),
            Line(588, 854, 103, 42, "STARTE"), Line(604, 898, 84, 40, "M SG"),
        };
        Assert.Equal(0, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.DropOversized(yankee));
    }

    [Fact]
    public void FewLines_AreNeverDroppedAsOversized()
    {
        var lines = new List<TranslationRegion> { Line(100, 100, 400, 80, "CHAPTER 1"), Line(100, 300, 600, 30, "The morning after.") };
        Assert.Equal(0, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.DropOversized(lines));
    }

    [Theory]
    [InlineData("JELLYCAT", "JELLYCAT", true)]
    [InlineData("HP 50", "HP 50", true)]
    [InlineData("ORCAMAD", "ORCA MAD", true)]
    [InlineData("SSR", "ssr.", true)]
    [InlineData("MERKOTA", "Меркота", false)]
    [InlineData("EXIT", "Выход", false)]
    [InlineData("", "", false)]
    public void UntranslatedText_IsDetected(string original, string translated, bool same) =>
        Assert.Equal(same, ErneyTranslateTool.Core.Ocr.OcrTextHelpers.IsUntranslated(original, translated));

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
    /// must replay (almost) without layout flicker. A new recording of a flickering
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
            var paths = new Dictionary<int, string>();
            var replay = LayoutReplay.Run(OcrRecorder.Load(file), (i, p) => paths[i] = p);
            var flicker = LayoutReplay.FlickerFrames(replay.Layouts);
            foreach (var f in flicker.Take(5))
                for (int i = Math.Max(0, f - 3); i <= f; i++)
                    _out.WriteLine($"  trace #{i}: {paths.GetValueOrDefault(i, "-")}");
            _out.WriteLine($"{Path.GetFileName(file)}: {replay.Layouts.Count} frames, flicker {flicker.Count}" +
                           (flicker.Count > 0 ? $" at #{string.Join(", #", flicker.Take(20))}" : ""));
            // One per hundred frames is allowed: OCR noise that shows up
            // for a single frame ("Oo") can't be told from new text in advance.
            // At least one even for a short recording: animated text such as
            // "Loading..." really changes on screen.
            if (flicker.Count > Math.Max(1, replay.Layouts.Count / 100))
            {
                _out.WriteLine(replay.Describe());
                failed.Add(Path.GetFileName(file));
            }
        }
        Assert.Empty(failed);
    }
}
