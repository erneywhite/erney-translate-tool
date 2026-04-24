using System.Windows;
using System.Windows.Controls;

namespace ErneyTranslateTool.Views.Controls;

/// <summary>
/// API-key / password style input. Masks the value as bullets by default,
/// with an eye-toggle button to temporarily reveal what was typed. Two-way
/// binding to <see cref="Secret"/> works regardless of which sub-textbox
/// is currently visible — both boxes share the same dependency property.
///
/// <para>We use two TextBoxes (one masked, one plain) instead of switching
/// a single PasswordBox to a TextBox because PasswordBox.Password isn't
/// directly bindable in WPF and the workarounds (attached property, code-
/// behind sync) are uglier than just toggling visibility on two boxes.</para>
/// </summary>
public partial class MaskedSecretBox : UserControl
{
    public static readonly DependencyProperty SecretProperty = DependencyProperty.Register(
        nameof(Secret),
        typeof(string),
        typeof(MaskedSecretBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSecretChanged));

    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    private bool _syncing;

    public MaskedSecretBox()
    {
        InitializeComponent();
        // Both child textboxes notify us on every keystroke so we keep the
        // dependency property authoritative regardless of focus order.
        MaskedBox.TextChanged += (_, _) => SyncFromBox(MaskedBox.Text);
        PlainBox.TextChanged  += (_, _) => SyncFromBox(PlainBox.Text);
    }

    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MaskedSecretBox self) self.SyncToBoxes((string?)e.NewValue ?? string.Empty);
    }

    /// <summary>Push the dependency-property value into both internal boxes — masked rendering for one, plain for the other.</summary>
    private void SyncToBoxes(string value)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            // Masked box shows bullets equal in count to the actual key —
            // gives a useful "typed something" indication without leaking
            // length precisely (we render as fixed dots regardless).
            MaskedBox.Text = new string('•', value?.Length ?? 0);
            PlainBox.Text = value ?? string.Empty;
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Read whatever's currently visible back into the Secret property.
    /// Only the plain box is the source of truth when revealed — the
    /// masked box's bullets are display-only.
    /// </summary>
    private void SyncFromBox(string text)
    {
        if (_syncing) return;
        // The masked box's TextChanged fires when we programmatically push
        // bullets via SyncToBoxes — guard against feeding bullets back into
        // Secret. If the visible content is exclusively bullets and matches
        // the current Secret length we treat it as a display-only update.
        if (!IsRevealed && IsAllBullets(text) && text.Length == (Secret?.Length ?? 0)) return;

        _syncing = true;
        try
        {
            Secret = text;
            // Keep the hidden box in sync so flipping visibility shows the
            // same value.
            if (IsRevealed) MaskedBox.Text = new string('•', text.Length);
            else PlainBox.Text = text;
        }
        finally { _syncing = false; }
    }

    private bool IsRevealed => RevealToggle.IsChecked == true;

    private static bool IsAllBullets(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        foreach (var c in s) if (c != '•') return false;
        return true;
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        if (IsRevealed)
        {
            PlainBox.Visibility = Visibility.Visible;
            MaskedBox.Visibility = Visibility.Collapsed;
            EyeIcon.Text = "🙈";
            PlainBox.Focus();
            PlainBox.SelectAll();
        }
        else
        {
            PlainBox.Visibility = Visibility.Collapsed;
            MaskedBox.Visibility = Visibility.Visible;
            EyeIcon.Text = "👁";
        }
    }
}
