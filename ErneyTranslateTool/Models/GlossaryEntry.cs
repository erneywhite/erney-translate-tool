using System.ComponentModel;
using System.Runtime.CompilerServices;

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
///
/// <para>
/// Implements INotifyPropertyChanged so WPF DataGrid inline edits are
/// committed back into the object — without it the grid enters edit mode
/// but the new text vanishes when the cell loses focus (this was the
/// "can't type into glossary fields" bug in v1.0.5).
/// </para>
/// </summary>
public class GlossaryEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private long _id;
    private string _sourceText = string.Empty;
    private string _targetText = string.Empty;
    private string _targetLanguage = "RU";
    private bool _isCaseSensitive;
    private bool _isWholeWord = true;
    private string _notes = string.Empty;

    /// <summary>Primary key, set by SQLite on insert.</summary>
    public long Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    /// <summary>What the translator produced that we want to overwrite.</summary>
    public string SourceText
    {
        get => _sourceText;
        set => Set(ref _sourceText, value);
    }

    /// <summary>What we want shown in the overlay instead.</summary>
    public string TargetText
    {
        get => _targetText;
        set => Set(ref _targetText, value);
    }

    /// <summary>
    /// Two-letter target language code (RU, EN, JA…) — must match
    /// <see cref="AppConfig.TargetLanguage"/> for the rule to fire.
    /// </summary>
    public string TargetLanguage
    {
        get => _targetLanguage;
        set => Set(ref _targetLanguage, value);
    }

    /// <summary>If true, only matches that respect letter case are replaced.</summary>
    public bool IsCaseSensitive
    {
        get => _isCaseSensitive;
        set => Set(ref _isCaseSensitive, value);
    }

    /// <summary>
    /// If true, the source text only matches when surrounded by word
    /// boundaries (so "Mer" doesn't replace inside "Merlin"). Defaults
    /// true — almost always what you want for proper nouns.
    /// </summary>
    public bool IsWholeWord
    {
        get => _isWholeWord;
        set => Set(ref _isWholeWord, value);
    }

    /// <summary>Optional free-form note shown in the editor (e.g. "from Witcher 3 patch 4.0").</summary>
    public string Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
