using System.Windows;
using ErneyTranslateTool.Core;

namespace ErneyTranslateTool.Tests;

public class ColorSamplerTests
{
    private const int Pink = unchecked((int)0xFFD8A0A8), Dark = unchecked((int)0xFF302830);

    /// <summary>100×40 pink frame with a "word": dark vertical strokes inside 20..80 × 10..30.</summary>
    private static ColorSampler.Pixels Frame(bool withText)
    {
        var px = new int[100 * 40];
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 100; x++)
            px[y * 100 + x] = withText && x is >= 20 and < 80 && y is >= 10 and < 30 && x % 4 < 2 ? Dark : Pink;
        return new ColorSampler.Pixels(px, 100, 100, 40);
    }

    [Fact]
    public void ReadsBackgroundAndLetters()
    {
        var (bg, text) = ColorSampler.SampleBlock(Frame(true), new Rect(20, 10, 60, 20));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xD8, 0xA0, 0xA8), bg);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x30, 0x28, 0x30), text);
    }

    [Fact]
    public void NoLetters_NoTextColour()
    {
        var (bg, text) = ColorSampler.SampleBlock(Frame(false), new Rect(20, 10, 60, 20));
        Assert.NotNull(bg);
        Assert.Null(text);
    }

    [Fact]
    public void BoxAtFrameEdge_DoesNotThrow()
    {
        var (bg, _) = ColorSampler.SampleBlock(Frame(true), new Rect(-5, -5, 30, 20));
        Assert.NotNull(bg);
        Assert.Equal((null, null), ColorSampler.SampleBlock(Frame(true), new Rect(200, 200, 10, 10)));
    }
}
