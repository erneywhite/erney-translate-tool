using System;
using System.Threading;
using System.Threading.Tasks;
using DeepL;
using Serilog;

namespace ErneyTranslateTool.Core.Translators;

public class DeepLTranslator : ITranslator
{
    public string Name => "DeepL";

    private readonly ILogger _logger;
    private readonly Translator _translator;
    private bool _disposed;

    public DeepLTranslator(string apiKey, ILogger logger)
    {
        _logger = logger;
        _translator = new Translator(apiKey);
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        var result = await _translator.TranslateTextAsync(text, null, targetLanguage, null, ct);
        return result.Text;
    }

    public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            var usage = await _translator.GetUsageAsync(ct);
            var msg = $"DeepL: ключ действителен.\n" +
                      $"Лимит: {usage.Character.Limit:N0} симв./мес.\n" +
                      $"Использовано: {usage.Character.Count:N0}";
            return (true, msg);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DeepL verify failed");
            return (false, $"DeepL: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _translator.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
