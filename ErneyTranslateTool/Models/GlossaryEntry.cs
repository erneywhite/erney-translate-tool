namespace ErneyTranslateTool.Models;

/// <summary>
/// One user-defined "force this to translate as that" rule. Applied AFTER
/// the translator returns its result — replaces every occurrence of
/// <see cref="SourceText"/> with <see cref="TargetText"/>, optionally
/// honouring case and word boundaries. Useful for proper nouns the
/// translator mangles (Geralt → Геральт, Kaer Morhen → Каэр Морхен).
///
/// <para>
/// Note: <see cref="TargetLanguage"/> is the user's <i>output</i> language —
/// rules are scoped to it so a Russian glossary doesn't accidentally
/// mangle English translations later.
/// </para>
/// </summary>
public class GlossaryEntry
{
    /// <summary>Primary key, set by SQLite on insert.</summary>
    public long Id { get; set; }

    /// <summary>What the translator produced that we want to overwrite.</summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>What we want shown in the overlay instead.</summary>
    public string TargetText { get; set; } = string.Empty;

    /// <summary>
    /// Two-letter target language code (RU, EN, JA…) — must match
    /// <see cref="AppConfig.TargetLanguage"/> for the rule to fire.
    /// </summary>
    public string TargetLanguage { get; set; } = "RU";

    /// <summary>If true, only matches that respect letter case are replaced.</summary>
    public bool IsCaseSensitive { get; set; } = false;

    /// <summary>
    /// If true, the source text only matches when surrounded by word
    /// boundaries (so "Mer" doesn't replace inside "Merlin"). Defaults
    /// true — almost always what you want for proper nouns.
    /// </summary>
    public bool IsWholeWord { get; set; } = true;

    /// <summary>Optional free-form note shown in the editor (e.g. "from Witcher 3 patch 4.0").</summary>
    public string Notes { get; set; } = string.Empty;
}
