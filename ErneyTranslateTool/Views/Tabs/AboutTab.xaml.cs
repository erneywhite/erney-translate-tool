using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ErneyTranslateTool.Views.Tabs;

public partial class AboutTab : UserControl
{
    public AboutTab()
    {
        InitializeComponent();
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.RunManualUpdateCheck();
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/erneywhite/erney-translate-tool")
            {
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://dalink.to/toristarm")
            {
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }
}
