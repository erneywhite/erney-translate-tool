using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Views.Tabs;

public partial class HistoryTab : UserControl
{
    public HistoryTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// WPF's DataGrid doesn't auto-select the row that gets right-clicked,
    /// so the ContextMenu's "{Binding SelectedItem}" would otherwise point
    /// at whatever was previously selected — a footgun. We promote the
    /// row under the cursor to be the selection right before the menu pops,
    /// matching the "right-click selects then opens menu" idiom users
    /// expect from File Explorer / VS Code / etc.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && row.DataContext is TranslationEntry entry)
        {
            EntriesGrid.SelectedItem = entry;
            // Don't mark e.Handled — the routed event still needs to bubble
            // up to actually open the ContextMenu attached to the DataGrid.
        }
    }
}
