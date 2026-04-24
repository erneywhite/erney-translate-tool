using System.Collections.Generic;
using System.Threading;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Optional capability that <see cref="ITranslator"/> implementations can
/// add to surface partial translations as they're being generated.
///
/// <para>For LLM providers (OpenAI, Anthropic) this maps directly onto
/// their server-sent events streams — instead of waiting 1-2 seconds for
/// the full reply, the user sees the translation appear word-by-word
/// starting ~200 ms after the request leaves. Time-to-first-token feels
/// dramatically more responsive even though total time is similar.</para>
///
/// <para>The orchestrator (<see cref="TranslationService"/>) checks for
/// this interface at dispatch time. Translators that don't implement it
/// fall through to the regular <see cref="ITranslator.TranslateAsync"/>
/// path unchanged, so non-LLM providers (DeepL, MyMemory, Google, Libre)
/// need no changes.</para>
/// </summary>
public interface IStreamingTranslator : ITranslator
{
    /// <summary>
    /// Translate <paramref name="text"/> into <paramref name="targetLanguage"/>,
    /// yielding the FULL accumulated translation each time a new server
    /// chunk arrives. Final element is the complete translation — same
    /// value <see cref="ITranslator.TranslateAsync"/> would have returned.
    ///
    /// <para>Each yielded value INCLUDES every chunk so far (not just the
    /// delta) so the caller can pass it straight to the overlay without
    /// having to accumulate.</para>
    /// </summary>
    IAsyncEnumerable<string> TranslateStreamAsync(
        string text,
        string targetLanguage,
        CancellationToken ct = default);
}
