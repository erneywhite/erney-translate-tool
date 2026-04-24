using System.Windows;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Views.Dialogs;

/// <summary>
/// One-shot "Geralt → Геральт" dialog opened from the History tab right-
/// click menu. Prefills both fields with the selected row's full sentence
/// so the user just trims them down to the proper noun rather than typing
/// from scratch — that's where most "I should add this to glossary"
/// frustration comes from in practice.
///
/// <para>The chosen text + checkbox flags are surfaced through
/// <see cref="Result"/> after a successful Save; the caller persists it
/// (<see cref="ErneyTranslateTool.Data.GlossaryRepository"/>) and
/// invalidates the live <see cref="ErneyTranslateTool.Core.Glossary.GlossaryApplier"/>.</para>
/// </summary>
public partial class AddToGlossaryDialog : Window
{
    public GlossaryEntry? Result { get; private set; }

    private readonly string _targetLanguage;

    public AddToGlossaryDialog(string sourcePrefill, string targetPrefill, string targetLanguage)
    {
        InitializeComponent();
        SourceBox.Text = sourcePrefill ?? string.Empty;
        TargetBox.Text = targetPrefill ?? string.Empty;
        _targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "RU" : targetLanguage;
        Loaded += (_, _) =>
        {
            // Focus the source box and select all so the user can immediately
            // start trimming with arrow keys / Backspace without an extra
            // click — they're going to edit it anyway.
            SourceBox.Focus();
            SourceBox.SelectAll();
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var src = SourceBox.Text?.Trim() ?? string.Empty;
        var tgt = TargetBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
        {
            // Soft validation: the rule needs both halves to be useful.
            // Don't close — let the user fix it without retyping the other field.
            MessageBox.Show(this,
                Core.LanguageManager.Get("Strings.AddToGlossary.MissingFields"),
                Core.LanguageManager.Get("Strings.AddToGlossary.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = new GlossaryEntry
        {
            SourceText = src,
            TargetText = tgt,
            TargetLanguage = _targetLanguage,
            IsCaseSensitive = CaseCheck.IsChecked == true,
            IsWholeWord = WholeWordCheck.IsChecked == true,
            Notes = Core.LanguageManager.Get("Strings.AddToGlossary.NoteFromHistory"),
        };
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
