using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Tests;

/// <summary>Cases from the Yankee Massage screenshots (coordinates from the recording).</summary>
public class RegionGrouperTests
{
    private static TranslationRegion L(double x, double y, double w, double h, string text) =>
        new() { Bounds = new Rect(x, y, w, h), OriginalText = text };

    private static List<string> Group(params TranslationRegion[] lines) =>
        RegionGrouper.Group(lines.ToList()).Select(r => r.OriginalText).ToList();

    [Fact]
    public void StackedMenuButtons_StaySeparate() =>
        Assert.Equal(new[] { "NORMAL", "HARD", "S.HARD" },
            Group(L(152, 77, 92, 25, "NORMAL"), L(155, 109, 88, 25, "HARD"), L(154, 142, 92, 26, "S.HARD")));

    [Fact]
    public void LanguageSelectorAndBgm_StaySeparate() =>
        Assert.Equal(new[] { "< ENGLISH", "BGM" },
            Group(L(72, 470, 175, 36, "< ENGLISH"), L(103, 541, 54, 35, "BGM")));

    [Fact]
    public void SignUnderAFinishedSentence_StaysSeparate() =>
        Assert.Equal(new[] { "But now, how and why is she here in my shop?", "MSG" },
            Group(L(437, 852, 746, 34, "But now, how and why is she here in my shop?"), L(604, 898, 84, 34, "MSG")));

    [Fact]
    public void DialogueWrap_StillMerges() =>
        Assert.Single(Group(
            L(440, 854, 818, 32, "Whether she doesn't recognize me or simply doesn't deem me"),
            L(440, 907, 850, 32, "worthy of acknowledgment, Dana sits on the waiting room sofa,"),
            L(440, 958, 400, 32, "casually awaiting her massage.")));

    [Fact]
    public void AllCapsParagraph_StillMerges() =>
        Assert.Equal(new[] { "WHERE DID YOU PUT THE KEY TO THE OLD HOUSE?" },
            Group(L(100, 100, 600, 30, "WHERE DID YOU PUT THE KEY TO"), L(100, 140, 260, 30, "THE OLD HOUSE?")));
}
