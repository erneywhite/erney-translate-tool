using System;
using System.Collections.Generic;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Single source of truth for LLM model ids: defaults, the presets shown in
/// the settings dropdowns, and the ids providers have shut down.
///
/// <para>v1.0.30: before this the lists lived in three places (AppConfig
/// defaults, SettingsViewModel presets, translator constructors) and went
/// stale together — the Gemini default <c>gemini-2.0-flash</c> was shut down
/// by Google, so Gemini stopped working for everyone who never touched the
/// model field. <see cref="MigrateRetired"/> moves such saved settings to a
/// live model on startup.</para>
///
/// <para>Checked against the providers' model and deprecation pages on
/// 2026-10-28. When a provider retires a model, add its id to the matching
/// <c>IsRetired*</c> check and, if it was a default, pick a new one.</para>
/// </summary>
public static class ModelCatalog
{
    public const string DefaultOpenAI = "gpt-6-luna";
    public const string DefaultAnthropic = "claude-haiku-5-5";
    public const string DefaultGemini = "gemini-3.5-flash-lite";
    public const string DefaultGroq = "openai/gpt-oss-120b";

    public static IReadOnlyList<string> OpenAIPresets { get; } = new[]
    {
        "gpt-6-luna",
        "gpt-6-astra",
    };

    public static IReadOnlyList<string> AnthropicPresets { get; } = new[]
    {
        "claude-haiku-5-5",
        "claude-sonnet-5-5",
        "claude-opus-5-5",
        "claude-haiku-4-5",
    };

    /// <summary>All have a free tier on AI Studio; Flash-Lite has the most generous limits.</summary>
    public static IReadOnlyList<string> GeminiPresets { get; } = new[]
    {
        "gemini-3.5-flash-lite",
        "gemini-3.6-flash",
        "gemini-3.8-flash",
        "gemini-3.1-flash-lite",
    };

    public static IReadOnlyList<string> GroqPresets { get; } = new[]
    {
        "openai/gpt-oss-120b",
        "openai/gpt-oss-20b",
    };

    /// <summary>
    /// Replace shut-down model ids in <paramref name="config"/> with live
    /// ones. Returns a human-readable line per change (for the log); empty
    /// when nothing changed.
    /// </summary>
    public static List<string> MigrateRetired(AppConfig config)
    {
        var changes = new List<string>();

        config.OpenAIModel = Migrate("OpenAI", config.OpenAIModel, ReplacementForOpenAI, changes);
        config.AnthropicModel = Migrate("Anthropic", config.AnthropicModel, ReplacementForAnthropic, changes);
        config.GeminiModel = Migrate("Gemini", config.GeminiModel, ReplacementForGemini, changes);
        config.GroqModel = Migrate("Groq", config.GroqModel, ReplacementForGroq, changes);

        return changes;
    }

    private static string Migrate(string provider, string current, Func<string, string?> replacementFor, List<string> changes)
    {
        var replacement = replacementFor(current ?? string.Empty);
        if (replacement == null) return current ?? string.Empty;
        changes.Add($"{provider}: {current} -> {replacement}");
        return replacement;
    }

    // Each returns the new id for a retired model, null if the model is fine.

    internal static string? ReplacementForOpenAI(string model) => model switch
    {
        // Shut down 2026-10-23.
        _ when model.StartsWith("gpt-3.5-turbo", StringComparison.Ordinal) => DefaultOpenAI,
        _ when model.StartsWith("gpt-4-turbo", StringComparison.Ordinal) => DefaultOpenAI,
        // gpt-4o / gpt-4o-mini / gpt-4.1-mini are deprecated but run until
        // 2027-01-14 — left alone so a working paid setup isn't switched to
        // a model the user never tried. Move them here after that date.
        _ => null,
    };

    internal static string? ReplacementForAnthropic(string model)
    {
        // Still served: Haiku 4.5, Sonnet 4.6, Opus 4.5+ and the 5.x line.
        // Everything older (3.x, Sonnet 4 / 4.5, Opus 4 / 4.1) is retired.
        bool retired = model.StartsWith("claude-3", StringComparison.Ordinal)
                       || model.StartsWith("claude-sonnet-4-5", StringComparison.Ordinal)
                       || model.StartsWith("claude-sonnet-4-2", StringComparison.Ordinal) // dated claude-sonnet-4-2025xxxx
                       || model == "claude-sonnet-4-0"
                       || model.StartsWith("claude-opus-4-1", StringComparison.Ordinal)
                       || model.StartsWith("claude-opus-4-2", StringComparison.Ordinal)
                       || model == "claude-opus-4-0";
        if (!retired) return null;
        // Keep the user's tier: a Sonnet user asked for quality over price.
        if (model.Contains("sonnet", StringComparison.Ordinal)) return "claude-sonnet-5-5";
        if (model.Contains("opus", StringComparison.Ordinal)) return "claude-opus-5-5";
        return DefaultAnthropic;
    }

    internal static string? ReplacementForGemini(string model) =>
        model.StartsWith("gemini-1.", StringComparison.Ordinal)
        || model.StartsWith("gemini-2.0", StringComparison.Ordinal)
        || model == "gemini-3.1-flash-lite-preview"
            ? DefaultGemini
            : null;

    internal static string? ReplacementForGroq(string model) => model switch
    {
        // Groq's recommended replacements (deprecations page).
        "llama-3.1-8b-instant" => "openai/gpt-oss-20b",
        "llama-3.3-70b-versatile" or "gemma2-9b-it" or "mixtral-8x7b-32768" => DefaultGroq,
        _ => null,
    };
}
