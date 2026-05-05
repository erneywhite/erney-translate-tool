using System.Windows;
using System.Windows.Controls;

namespace ErneyTranslateTool.Views.Controls;

/// <summary>
/// API-key / password style input. Bullets the value by default; the eye
/// toggle reveals it for verification.
///
/// <para>Design (v1.0.19 rewrite): real <see cref="PasswordBox"/> for
/// masked mode, <see cref="TextBox"/> for revealed mode, swap visibility
/// on the eye toggle. The pre-v1.0.19 version simulated masking with a
/// second <see cref="TextBox"/> filled with bullet characters — that had
/// a subtle race where the eye-toggle wouldn't actually hide a freshly-
/// loaded key (the masked TextBox could end up holding the literal value
/// instead of bullets, depending on initialisation order). PasswordBox
/// has masking built into its rendering so it's impossible to leak the
/// value visually no matter what state the code is in.</para>
///
/// <para>The price of using PasswordBox is that <c>Password</c> is not a
/// dependency property — we sync it with <see cref="Secret"/> manually
/// via <see cref="PasswordBox.PasswordChanged"/> + a guard flag to avoid
/// re-entry loops. Same trick the codebase already uses elsewhere.</para>
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

    /// <summary>The actual key/password. Two-way bindable.</summary>
    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    private bool _syncing;

    public MaskedSecretBox()
    {
        InitializeComponent();
        // Both child controls notify us on any change. _syncing prevents
        // re-entrant updates when we're the ones doing the writing.
        MaskedBox.PasswordChanged += (_, _) => SyncFromMasked();
        PlainBox.TextChanged      += (_, _) => SyncFromPlain();
    }

    /// <summary>External setter (binding source changed) — push the new value into both child controls.</summary>
    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MaskedSecretBox self) self.SyncToBoxes((string?)e.NewValue ?? string.Empty);
    }

    private void SyncToBoxes(string value)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            // Only update each control if it's actually out of sync — avoids
            // moving the caret unnecessarily on every keystroke.
            if (MaskedBox.Password != value) MaskedBox.Password = value;
            if (PlainBox.Text != value) PlainBox.Text = value;
        }
        finally { _syncing = false; }
    }

    private void SyncFromMasked()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Secret = MaskedBox.Password;
            // Mirror into the plain box so flipping reveal shows the same value.
            if (PlainBox.Text != MaskedBox.Password) PlainBox.Text = MaskedBox.Password;
        }
        finally { _syncing = false; }
    }

    private void SyncFromPlain()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Secret = PlainBox.Text;
            if (MaskedBox.Password != PlainBox.Text) MaskedBox.Password = PlainBox.Text;
        }
        finally { _syncing = false; }
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        var revealed = RevealToggle.IsChecked == true;
        if (revealed)
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
