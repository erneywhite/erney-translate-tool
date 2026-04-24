using System;
using System.Linq;
using System.Windows;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Swaps the active theme ResourceDictionary in App.Current.Resources at
/// runtime. Each theme dictionary lives in Resources/Themes/{Name}.xaml
/// and only contains brush definitions (BackgroundBrush, TextPrimaryBrush,
/// etc.); styles in Styles.xaml resolve them via DynamicResource so the
/// whole UI re-renders on swap without a restart.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Nord = "Nord";

    public static readonly (string Id, string DisplayName)[] Available =
    {
        (Dark,  "Тёмная (по умолчанию)"),
        (Light, "Светлая"),
        (Nord,  "Nord (холодная синяя)"),
    };

    public static void Apply(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId)) themeId = Dark;
        if (!Available.Any(t => t.Id == themeId)) themeId = Dark;

        var app = Application.Current;
        if (app == null) return;

        var newDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Themes/{themeId}.xaml", UriKind.Absolute)
        };

        // Remove any previously installed theme dictionary (matched by the
        // /Themes/ folder in the source URI).
        var dicts = app.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString;
            if (src != null && src.Contains("/Themes/", StringComparison.OrdinalIgnoreCase))
                dicts.RemoveAt(i);
        }
        // New theme goes first so it sits below Styles.xaml in the lookup
        // chain (Styles.xaml is loaded next; brushes still resolve via
        // DynamicResource lookup which walks all merged dictionaries).
        dicts.Insert(0, newDict);
    }
}
