using ErneyTranslateTool.Core.Translators;

namespace ErneyTranslateTool.Tests;

public class CustomLlmTests
{
    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    [InlineData("http://localhost:1234/v1/", "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234/v1/chat/completions", "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234/v1/models", "http://localhost:1234/v1")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1")]
    [InlineData("  https://api.deepseek.com  ", "https://api.deepseek.com/v1")]
    public void ApiRoot_Normalises(string input, string expected) =>
        Assert.Equal(expected, OpenAITranslator.ApiRoot(input));

    [Fact]
    public void ChatCompletionsUrl_AppendsPath() =>
        Assert.Equal("http://localhost:11434/v1/chat/completions",
            OpenAITranslator.ChatCompletionsUrl("http://localhost:11434"));

    [Theory]
    [InlineData("Привет", "Привет")]
    [InlineData("<think>reasoning\nmore</think>Привет", "Привет")]
    [InlineData("<think>still thinking", "")]                 // unclosed: hide everything
    [InlineData("Привет<thi", "Привет")]                     // half-received tag held back
    [InlineData("a<think>x</think>b<think>y</think>c", "abc")]
    [InlineData("1 < 2", "1 < 2")]                          // a lone "<" with a space isn't a tag start
    public void StripThinking(string input, string expected) =>
        Assert.Equal(expected, OpenAITranslator.StripThinking(input));
}
