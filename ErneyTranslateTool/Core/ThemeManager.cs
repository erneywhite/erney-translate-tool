using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Swaps the active theme ResourceDictionary in App.Current.Resources at
/// runtime. Each theme dictionary lives in Resources/Themes/{Name}.xaml
/// and only contains brush definitions (BackgroundBrush, TextPrimaryBrush,
/// etc.); styles in Styles.xaml resolve them via DynamicResource so the
/// whole UI re-renders on swap without a restart.
///
/// The "Auto" id is a meta-theme that resolves to Light or Dark based on
/// the current Windows app-mode setting and re-resolves whenever the user
/// flips the system theme — see <see cref="OnSystemThemeChanged"/>.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Nord = "Nord";
    public const string CatppuccinMocha = "CatppuccinMocha";
    public const string TokyoNight = "TokyoNight";
    public const string Auto = "Auto";

    public static readonly (string Id, string DisplayName)[] Available =
    {
        (Auto,            "Авто (по системе Windows)"),
        (Dark,            "Тёмная (по умолчанию)"),
        (Light,           "Светлая"),
        (Nord,            "Nord (холодная синяя)"),
        (CatppuccinMocha, "Catppuccin Mocha (мягкие пастели)"),
        (TokyoNight,      "Tokyo Night (электрический синий)"),
    };

    // Track the user's selected id (may be "Auto"). The actual resolved
    // theme can differ — kept separately so we re-apply the right one when
    // the OS flips between dark and light.
    private static string _selectedId = Dark;
    private static bool _watcherWired;

    public static void Apply(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId)) themeId = Dark;
        if (!Available.Any(t => t.Id == themeId)) themeId = Dark;

        _selectedId = themeId;
        EnsureSystemWatcherWired();

        var resolved = themeId == Auto ? DetectSystemTheme() : themeId;
        ApplyResolved(resolved);
    }

    /// <summary>
    /// Look up Windows' "apps-use-light-theme" setting. Returns Dark/Light;
    /// any read failure falls back to Dark (least disruptive on a dark UI).
    /// </summary>
    public static string DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 1 ? Light : Dark;
        }
        catch
        {
            // Registry can be unavailable in unusual sessions — never throw
            // from a UI-init helper.
        }
        return Dark;
    }

    private static void ApplyResolved(string themeId)
    {
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

    /// <summary>
    /// Subscribe (once) to system-theme changes so "Auto" mode flips Live
    /// when the user toggles Windows between Light and Dark.
    /// </summary>
    private static void EnsureSystemWatcherWired()
    {
        if (_watcherWired) return;
        _watcherWired = true;
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

    private static void OnSystemThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // UserPreferenceChanged fires for many categories; we only care
        // about General which covers app-mode/colour switches.
        if (e.Category != UserPreferenceCategory.General) return;
        if (_selectedId != Auto) return;

        var app = Application.Current;
        if (app == null) return;

        // Always marshal back to the UI thread — registry reads + WPF
        // resource dictionary swaps must happen there.
        app.Dispatcher.BeginInvoke(new Action(() => ApplyResolved(DetectSystemTheme())));
    }
}
