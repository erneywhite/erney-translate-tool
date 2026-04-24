using System;
using System.Linq;
using System.Windows;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Swaps the active UI-strings ResourceDictionary in
/// App.Current.Resources at runtime, mirroring the way <see cref="ThemeManager"/>
/// switches colour palettes. Strings.{lang}.xaml dictionaries hold every
/// user-facing string keyed by a stable id; XAML resolves them via
/// <c>{DynamicResource Strings.SomeKey}</c>, so flipping the active
/// language re-renders the whole UI in place without a restart.
/// </summary>
public static class LanguageManager
{
    public const string Russian = "ru";
    public const string English = "en";

    public static readonly (string Id, string DisplayName)[] Available =
    {
        (Russian, "Русский"),
        (English, "English"),
    };

    private static string _currentId = Russian;

    public static string CurrentId => _currentId;

    public static void Apply(string languageId)
    {
        if (string.IsNullOrWhiteSpace(languageId)) languageId = Russian;
        if (!Available.Any(l => l.Id == languageId)) languageId = Russian;

        _currentId = languageId;

        var app = Application.Current;
        if (app == null) return;

        var newDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{languageId}.xaml", UriKind.Absolute)
        };

        // Remove any previously installed strings dictionary (matched by
        // "Strings." in the source URI). Keeps the merged-dictionaries
        // collection clean across language flips.
        var dicts = app.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString;
            if (src != null && src.Contains("Strings.", StringComparison.OrdinalIgnoreCase))
                dicts.RemoveAt(i);
        }
        // Insert near the front so subsequent merged dictionaries (themes,
        // styles) can override anything that uses the same key.
        dicts.Insert(0, newDict);
    }

    /// <summary>
    /// Look up a string by key from the live merged dictionaries. Used by
    /// non-XAML callers (status messages from services, dialog text from
    /// ViewModels) that can't bind to a DynamicResource directly.
    /// Returns the key itself when no match is found — that way missing
    /// keys are obvious during development without crashing the app.
    /// </summary>
    public static string Get(string key)
    {
        var app = Application.Current;
        if (app == null) return key;
        return app.TryFindResource(key) as string ?? key;
    }

    /// <summary>
    /// <see cref="Get(string)"/> + <see cref="string.Format(string, object[])"/>
    /// in one call, for "X of Y" style strings that take parameters.
    /// </summary>
    public static string Format(string key, params object[] args)
    {
        var template = Get(key);
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }
}
