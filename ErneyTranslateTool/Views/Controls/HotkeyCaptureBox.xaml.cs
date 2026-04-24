using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ErneyTranslateTool.Core;

namespace ErneyTranslateTool.Views.Controls;

/// <summary>
/// Click-to-capture hotkey input. Replaces the v1.0.13-and-earlier raw
/// TextBox: the user clicked into a textbox and typed "Ctrl+Shift+T" by
/// hand (and discovered the change didn't take effect until restart —
/// which is the auto-reregistration bug fixed in MainWindow).
///
/// <para>Click → enter capture mode → press desired combo → it's stored.
/// Esc cancels back to the previous value, the small ✕ unsets entirely
/// (useful for the optional Pause hotkey).</para>
///
/// <para>The control validates that at least one modifier is present —
/// a global hotkey without one would intercept normal typing in any
/// focused application, which would be terrible UX.</para>
/// </summary>
public partial class HotkeyCaptureBox : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(string),
        typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHotkeyChanged));

    /// <summary>The "Ctrl+Shift+T" style string. Two-way bindable.</summary>
    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private bool _capturing;
    private string _previousHotkey = string.Empty;

    public HotkeyCaptureBox()
    {
        InitializeComponent();
        UpdateDisplay();
        // React to language flips in case the placeholder text differs
        // (e.g. "(не задано)" → "(not set)" while the box is on screen).
        LanguageManager.LanguageChanged += (_, _) =>
            Application.Current?.Dispatcher.Invoke(UpdateDisplay);
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyCaptureBox self) self.UpdateDisplay();
    }

    /// <summary>
    /// Repaint the visible label + clear-button visibility based on whether
    /// we're currently capturing and whether <see cref="Hotkey"/> is set.
    /// </summary>
    private void UpdateDisplay()
    {
        var resources = Application.Current?.Resources;

        if (_capturing)
        {
            DisplayText.Text = LanguageManager.Get("Strings.Hotkey.PressCombo");
            DisplayText.Opacity = 0.75;
            ClearButton.Visibility = Visibility.Collapsed;
            if (resources?["PrimaryBrush"] is Brush primary)
                CaptureSurface.BorderBrush = primary;
        }
        else
        {
            var hk = Hotkey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(hk))
            {
                DisplayText.Text = LanguageManager.Get("Strings.Hotkey.NotSet");
                DisplayText.Opacity = 0.5;
                ClearButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                DisplayText.Text = hk;
                DisplayText.Opacity = 1.0;
                ClearButton.Visibility = Visibility.Visible;
            }
            if (resources?["BorderBrush"] is Brush border)
                CaptureSurface.BorderBrush = border;
        }
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturing) return;
        _previousHotkey = Hotkey ?? string.Empty;
        _capturing = true;
        UpdateDisplay();

        // Take keyboard focus so PreviewKeyDown fires here. Without this the
        // outer window keeps focus and our capture handler never runs.
        Focus();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // Bypass capture mode — user explicitly wants the hotkey unset.
        Hotkey = string.Empty;
        _capturing = false;
        UpdateDisplay();
        e.Handled = true;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Click-away while capturing = cancel, restore previous value.
        if (_capturing)
        {
            _capturing = false;
            Hotkey = _previousHotkey;
            UpdateDisplay();
        }
    }

    /// <summary>
    /// Swallow every key event while capturing so Tab/Enter/Esc/Alt
    /// combos don't leak to surrounding controls. Resolves the modifier
    /// bitmask from <see cref="Keyboard.Modifiers"/> and the actual key
    /// from <see cref="KeyEventArgs.Key"/> (or SystemKey when Alt is held —
    /// WPF reports e.Key as Key.System in that case).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // ESC bails out without committing.
        if (key == Key.Escape)
        {
            _capturing = false;
            Hotkey = _previousHotkey;
            UpdateDisplay();
            return;
        }

        // Ignore the standalone modifier keys themselves — they arrive
        // here as we wait for the "real" key. Just keep waiting.
        if (key is Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt
                 or Key.LWin or Key.RWin
                 or Key.Tab) // Tab without modifiers shouldn't trigger
            return;

        var mods = Keyboard.Modifiers;
        // Backspace/Delete with no modifiers = clear the hotkey while in
        // capture mode (alternative to the ✕ button — convenient when
        // hands are already on the keyboard).
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            _capturing = false;
            Hotkey = string.Empty;
            UpdateDisplay();
            return;
        }

        // No modifier = invalid — global hotkey without one would steal
        // every keystroke from every focused app. Just keep waiting.
        if (mods == ModifierKeys.None) return;

        var formatted = HotkeyParser.Format(mods, key);
        if (string.IsNullOrEmpty(formatted)) return;
        // Refuse modifier-only combos — Format returns empty for those,
        // but be defensive in case Format ever loosens.
        if (!HotkeyParser.TryParse(formatted, out _, out _)) return;

        _capturing = false;
        Hotkey = formatted;
        UpdateDisplay();
    }
}
