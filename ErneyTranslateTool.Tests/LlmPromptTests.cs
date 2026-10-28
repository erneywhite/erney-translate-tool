using ErneyTranslateTool.Core.Translators;

namespace ErneyTranslateTool.Tests;

public class LlmPromptTests
{
    [Theory]
    [InlineData("I'm sorry, but I can't help with that.", "Whatever the line was", true)]
    [InlineData("I’m sorry, but I can’t help with that.", "Some line", true)]
    [InlineData("I cannot translate this content.", "Some line", true)]
    [InlineData("Извините, но я не могу помочь с этим.", "Some line", true)]
    [InlineData("Извини, я опоздал.", "Sorry, I'm late.", false)]               // a real translation
    [InlineData("I'm sorry for being late.", "I'm sorry for being late.", false)] // the game line itself
    [InlineData("Простите меня.", "Forgive me.", false)]
    public void Refusal_IsDetected(string translated, string original, bool refusal) =>
        Assert.Equal(refusal, LlmPrompt.IsRefusal(translated, original));

    [Fact]
    public void Prompt_AsksForFaithfulTranslationOfFiction() =>
        Assert.Contains("without refusing", LlmPrompt.System("Russian"));
}
