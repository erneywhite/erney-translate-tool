using System;
using System.Threading;
using System.Threading.Tasks;

namespace ErneyTranslateTool.Core.Translators;

/// <summary>
/// Abstraction over a translation backend. Implementations handle their own
/// language-code conversion (input is the user-selected DeepL-style code).
/// </summary>
public interface ITranslator : IDisposable
{
    string Name { get; }
    Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default);
    Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default);
}
