using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core.Glossary;

/// <summary>
/// Applies user-defined glossary rules to a translated string. Designed to
/// be safe in the hot translate path: every rule is wrapped in a try/catch,
/// rules are cached and only refreshed when the repo signals dirty, and a
/// global kill-switch (<see cref="AppConfig.GlossaryEnabled"/>) lets the
/// user turn it all off without deleting anything.
/// </summary>
public class GlossaryApplier
{
    private readonly GlossaryRepository _repo;
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    // Compiled rule cache keyed by target language. Rebuilt whenever the
    // repo signals a write. Lock is short-held so the hot translate path
    // doesn't stall on edits.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, List<CompiledRule>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _dirty = 1; // start dirty so first call populates

    public GlossaryApplier(GlossaryRepository repo, AppSettings settings, ILogger logger)
    {
        _repo = repo;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Mark the cache stale — call after any add/edit/delete in the UI.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// Apply all rules for the current target language to <paramref name="text"/>.
    /// Returns the original string if the master toggle is off, the input
    /// is empty, or a rule throws (defensive — bad regex shouldn't break
    /// translation).
    /// </summary>
    public string Apply(string text, string targetLanguage)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!_settings.Config.GlossaryEnabled) return text;

        try
        {
            var rules = GetRulesFor(targetLanguage);
            if (rules.Count == 0) return text;

            var result = text;
            foreach (var rule in rules)
            {
                try
                {
                    result = rule.Regex.Replace(result, rule.Replacement);
                }
                catch (Exception ex)
                {
                    // Don't let one bad rule poison the rest of the pass.
                    _logger.Warning(ex,
                        "Glossary rule failed; skipping. src='{Src}' -> dst='{Dst}'",
                        rule.Source, rule.Replacement);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GlossaryApplier crashed; returning original text");
            return text;
        }
    }

    private List<CompiledRule> GetRulesFor(string targetLanguage)
    {
        // Fast path: cache fresh and language already compiled.
        if (Volatile.Read(ref _dirty) == 0)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(targetLanguage, out var cached)) return cached;
            }
        }

        // Slow path: refill caches. We rebuild lazily — only languages that
        // actually get translations spend any cycles compiling regexes.
        lock (_cacheLock)
        {
            if (Volatile.Read(ref _dirty) != 0)
            {
                _cache.Clear();
                Interlocked.Exchange(ref _dirty, 0);
            }

            if (_cache.TryGetValue(targetLanguage, out var cached)) return cached;

            var raw = _repo.GetForLanguage(targetLanguage);
            var compiled = new List<CompiledRule>(raw.Count);
            foreach (var entry in raw)
            {
                if (string.IsNullOrEmpty(entry.SourceText)) continue;
                try
                {
                    compiled.Add(Compile(entry));
                }
                catch (Exception ex)
                {
                    // Skip rules with regex-incompatible source text rather
                    // than failing the whole batch.
                    _logger.Warning(ex,
                        "Could not compile glossary rule Id={Id} src='{Src}'",
                        entry.Id, entry.SourceText);
                }
            }
            _cache[targetLanguage] = compiled;
            return compiled;
        }
    }

    private static CompiledRule Compile(GlossaryEntry entry)
    {
        // Escape the source so user-typed "C++" or "1.0" doesn't get
        // interpreted as a regex pattern.
        var escaped = Regex.Escape(entry.SourceText);

        // Whole-word: bracket with \b on both sides. \b is Unicode-aware in
        // .NET when RegexOptions.None — works fine for "Геральт".
        var pattern = entry.IsWholeWord ? $@"\b{escaped}\b" : escaped;
        var options = entry.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        // Compiled because rules are reused per session; small upfront
        // cost, faster steady-state.
        options |= RegexOptions.Compiled;

        // Replacement string can contain $1, $&, etc. — we don't want
        // that interpretation, the user just typed literal text.
        var replacement = entry.TargetText.Replace("$", "$$");

        return new CompiledRule(entry.SourceText, replacement, new Regex(pattern, options));
    }

    private sealed record CompiledRule(string Source, string Replacement, Regex Regex);
}
