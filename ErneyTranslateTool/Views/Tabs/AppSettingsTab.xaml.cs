using System.Windows.Controls;

namespace ErneyTranslateTool.Views.Tabs;

/// <summary>
/// Application-level settings (theme, UI language, window behaviour,
/// autostart). Split out from OverlaySettingsTab in v1.0.12 hotfix so
/// the Overlay tab is genuinely about the overlay's appearance and not
/// a kitchen sink of unrelated preferences.
/// </summary>
public partial class AppSettingsTab : UserControl
{
    public AppSettingsTab()
    {
        InitializeComponent();
    }
}
