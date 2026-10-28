using ErneyTranslateTool.Core.Translators;

namespace ErneyTranslateTool.Tests;

public class MyMemoryTests
{
    [Fact]
    public void ShortText_IsOnePiece() =>
        Assert.Single(MyMemoryTranslator.SplitForLimit("Hello there.", 450));

    [Fact]
    public void LongText_SplitsAfterSentences_UnderTheLimit()
    {
        var sentence = "The bot, the background job worker and the scheduler all run in one Python process. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 12)).Trim();

        var parts = MyMemoryTranslator.SplitForLimit(text, 450);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= 450, $"{p.Length} chars"));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(text.Replace(" ", ""), string.Concat(parts).Replace(" ", ""));
    }

    [Fact]
    public void NoSentenceEnd_SplitsAtSpaces()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));
        var parts = MyMemoryTranslator.SplitForLimit(text, 450);
        Assert.All(parts, p => Assert.True(p.Length <= 450));
        Assert.All(parts, p => Assert.DoesNotContain("  ", p));
        Assert.Equal(200, parts.Sum(p => p.Split(' ').Length));
    }
}
