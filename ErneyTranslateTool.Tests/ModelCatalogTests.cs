using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void Defaults_AreNotRetired()
    {
        Assert.Null(ModelCatalog.ReplacementForOpenAI(ModelCatalog.DefaultOpenAI));
        Assert.Null(ModelCatalog.ReplacementForAnthropic(ModelCatalog.DefaultAnthropic));
        Assert.Null(ModelCatalog.ReplacementForGemini(ModelCatalog.DefaultGemini));
        Assert.Null(ModelCatalog.ReplacementForGroq(ModelCatalog.DefaultGroq));
    }

    [Fact]
    public void Presets_AreNotRetired()
    {
        Assert.All(ModelCatalog.OpenAIPresets, m => Assert.Null(ModelCatalog.ReplacementForOpenAI(m)));
        Assert.All(ModelCatalog.AnthropicPresets, m => Assert.Null(ModelCatalog.ReplacementForAnthropic(m)));
        Assert.All(ModelCatalog.GeminiPresets, m => Assert.Null(ModelCatalog.ReplacementForGemini(m)));
        Assert.All(ModelCatalog.GroqPresets, m => Assert.Null(ModelCatalog.ReplacementForGroq(m)));
    }

    [Fact]
    public void MigrateRetired_ReplacesOldDefaultsAndKeepsLiveModels()
    {
        var config = new AppConfig
        {
            OpenAIModel = "gpt-4o-mini",          // deprecated, still served -> kept
            AnthropicModel = "claude-3-5-sonnet-latest",
            GeminiModel = "gemini-2.0-flash",      // the old default, shut down
            GroqModel = "llama-3.1-8b-instant",
        };

        var changes = ModelCatalog.MigrateRetired(config);

        Assert.Equal(3, changes.Count);
        Assert.Equal("gpt-4o-mini", config.OpenAIModel);
        Assert.Equal("claude-sonnet-5-5", config.AnthropicModel); // tier kept
        Assert.Equal(ModelCatalog.DefaultGemini, config.GeminiModel);
        Assert.Equal("openai/gpt-oss-20b", config.GroqModel);
        Assert.Empty(ModelCatalog.MigrateRetired(config)); // idempotent
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-5-5")]
    [InlineData("claude-opus-4-1", "claude-opus-5-5")]
    [InlineData("claude-3-5-haiku-latest", ModelCatalog.DefaultAnthropic)]
    [InlineData("claude-sonnet-4-6", null)]
    [InlineData("claude-opus-4-5", null)]
    [InlineData("claude-haiku-4-5", null)]
    public void ReplacementForAnthropic(string model, string? expected) =>
        Assert.Equal(expected, ModelCatalog.ReplacementForAnthropic(model));

    [Theory]
    [InlineData("claude-3-5-sonnet-latest", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData("claude-sonnet-4-5-20250929", true)]
    [InlineData("claude-opus-4-20250514", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-haiku-5-5", false)]
    [InlineData("claude-sonnet-5-5", false)]
    public void Anthropic_TakesSamplingParams(string model, bool expected) =>
        Assert.Equal(expected, AnthropicTranslator.TakesSamplingParams(model));

    [Theory]
    [InlineData("gpt-4o-mini", null)]
    [InlineData("gpt-6-luna", "none")]
    [InlineData("gpt-5.1", "none")]
    [InlineData("gpt-5-mini", "minimal")]
    [InlineData("o4-mini", "low")]
    public void OpenAI_ReasoningEffortFor(string model, string? expected) =>
        Assert.Equal(expected, OpenAITranslator.ReasoningEffortFor(model));

    [Theory]
    [InlineData("gemini-3.5-flash-lite", "minimal")]
    [InlineData("gemini-3.8-flash", "low")]
    [InlineData("gemini-2.5-flash", null)]
    public void Gemini_ThinkingLevelFor(string model, string? expected) =>
        Assert.Equal(expected, GeminiTranslator.ThinkingLevelFor(model));
}
