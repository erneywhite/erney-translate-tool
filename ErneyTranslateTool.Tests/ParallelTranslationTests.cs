using System.Diagnostics;
using System.IO;
using System.Windows;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Glossary;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Tests;

public sealed class ParallelTranslationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ett-par-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private readonly TranslationService _service;

    public ParallelTranslationTests()
    {
        Directory.CreateDirectory(_dir);
        var settings = new AppSettings(_dir, _log);
        _service = new TranslationService(_log, settings,
            new CacheRepository(_dir, _log), new HistoryRepository(_dir, _log),
            new GlossaryApplier(new GlossaryRepository(_dir, _log), settings, _log));
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class SlowTranslator(string name, int delayMs, bool fail = false) : ITranslator
    {
        private int _running, _peak, _calls;
        public int Peak => _peak;
        public int Calls => _calls;
        public string Name => name;

        public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _running);
            InterlockedMax(ref _peak, now);
            try
            {
                await Task.Delay(delayMs, ct);
                if (fail) throw new InvalidOperationException("down");
                return $"[{name}] {text}";
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default) => Task.FromResult((true, ""));
        public void Dispose() { }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private static List<TranslationRegion> Lines(int n) => Enumerable.Range(0, n)
        .Select(i => new TranslationRegion { Bounds = new Rect(0, i * 30, 300, 25), OriginalText = $"line {i}" })
        .ToList();

    [Fact]
    public async Task Blocks_GoOutInParallel_AndKeepTheirOrder()
    {
        var t = new SlowTranslator("p", 300);
        _service.UseTranslatorsForTests(t, null);

        var sw = Stopwatch.StartNew();
        var result = await _service.TranslateRegionsAsync(Lines(6), "RU");
        sw.Stop();

        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"[p] line {i}"), result.Select(r => r.TranslatedText));
        Assert.InRange(t.Peak, 2, 3);                      // at most MaxParallelRequests at once
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms"); // one by one: 1800+
    }

    private sealed class RefusingTranslator : ITranslator
    {
        public string Name => "refuser";
        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default) =>
            Task.FromResult("I'm sorry, but I can't help with that.");
        public Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct = default) => Task.FromResult((true, ""));
        public void Dispose() { }
    }

    [Fact]
    public async Task RefusedLine_IsTranslatedByTheFallback()
    {
        _service.UseTranslatorsForTests(new RefusingTranslator(), new SlowTranslator("f", 10));
        var result = await _service.TranslateRegionsAsync(Lines(2), "RU");
        Assert.All(result, r => Assert.StartsWith("[f]", r.TranslatedText));
    }

    [Fact]
    public async Task FailedTranslation_IsReported_AndClearedAfterSuccess()
    {
        var reported = 0;
        _service.TranslatorProblem += (_, _) => Interlocked.Increment(ref reported);

        _service.UseTranslatorsForTests(new SlowTranslator("p", 10, fail: true), null);
        Assert.Empty(await _service.TranslateRegionsAsync(Lines(3), "RU"));
        Assert.NotNull(_service.LastProblem);
        Assert.Equal(1, reported); // three failures, one message (throttled)

        _service.UseTranslatorsForTests(new SlowTranslator("p", 10), null);
        Assert.Equal(3, (await _service.TranslateRegionsAsync(Lines(3), "RU")).Count);
        Assert.Null(_service.LastProblem);
    }

    [Fact]
    public async Task PrimaryFailingInParallel_SwitchesToFallbackOnce()
    {
        var primary = new SlowTranslator("p", 50, fail: true);
        var fallback = new SlowTranslator("f", 50);
        _service.UseTranslatorsForTests(primary, fallback);
        var switches = 0;
        _service.FallbackStateChanged += (_, _) => Interlocked.Increment(ref switches);

        await _service.TranslateRegionsAsync(Lines(3), "RU");   // three failures in parallel
        var second = await _service.TranslateRegionsAsync(Lines(3), "RU");

        Assert.Equal(1, switches);
        Assert.True(_service.IsUsingFallback);
        Assert.All(second, r => Assert.StartsWith("[f]", r.TranslatedText));
    }
}
