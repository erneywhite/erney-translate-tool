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
    public void CjkMenu_StaysSeparate() =>
        Assert.Equal(new[] { "新遊戲", "載入", "設定", "相冊", "退出" },
            Group(L(1021, 279, 74, 28, "新遊戲"), L(1021, 342, 47, 29, "載入"), L(1021, 406, 50, 29, "設定"),
                  L(1019, 469, 52, 29, "相冊"), L(1019, 533, 52, 27, "退出")));

    [Fact]
    public void CjkDialogueWrap_StillMerges() =>
        Assert.Single(Group(
            L(174, 576, 409, 25, "在人類的歷史記載中，「妖狐」不僅止是"),
            L(174, 610, 300, 25, "一種傳說中的生物")));

    [Fact]
    public void TitleCaseMenu_StaysSeparate() =>
        Assert.Equal(new[] { "Continue", "New Game", "Gallery", "Settings", "Exit" },
            Group(L(253, 408, 92, 22, "Continue"), L(243, 462, 112, 23, "New Game"), L(262, 513, 74, 28, "Gallery"),
                  L(256, 569, 88, 26, "Settings"), L(278, 622, 41, 25, "Exit")));

    [Fact]
    public void ShortDialogueTail_StillMerges() =>
        Assert.Single(Group(
            L(100, 100, 700, 30, "I never thought I would see her again after all"),
            L(100, 140, 200, 30, "these years")));

    [Fact]
    public void LineCutIntoPieces_ComesBackInReadingOrder() =>
        // Fatal Countdown news: OCR split the second line, its right piece 10 px higher.
        Assert.Equal(new[] { "The government is about to implement martial law Flash news, emergency Internet to be blocked" },
            Group(L(125, 800, 1393, 52, "The government is about to implement martial law"),
                  L(604, 868, 448, 49, "Internet to be blocked"),
                  L(101, 877, 491, 47, "Flash news, emergency")));

    [Fact]
    public void CapsHeadingAndTag_StayOffTheText() =>
        Assert.Equal(new[] { "BREAKING NEWS", "The government is about to implement martial law Flash news, emergency announcement.", "RIGHT NOW" },
            Group(L(158, 693, 437, 52, "BREAKING NEWS"),
                  L(154, 772, 1348, 54, "The government is about to implement martial law"),
                  L(128, 838, 929, 55, "Flash news, emergency announcement."),
                  L(122, 920, 218, 40, "RIGHT NOW")));

    [Fact]
    public void ButtonsOnOneRow_StaySeparate() =>
        Assert.Equal(new[] { "< ENGLISH", "BGM", "SE" },
            Group(L(72, 470, 175, 36, "< ENGLISH"), L(103, 541, 54, 35, "BGM"), L(263, 542, 36, 32, "SE")));

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
