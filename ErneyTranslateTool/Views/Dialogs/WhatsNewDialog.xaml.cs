using System;
using System.Diagnostics;
using System.Windows;
using Serilog;

namespace ErneyTranslateTool.Views.Dialogs;

/// <summary>
/// Lightweight "release notes" dialog shown once after the user upgrades.
/// </summary>
public partial class WhatsNewDialog : Window
{
    private readonly string _releaseUrl;
    private readonly ILogger _logger;

    public WhatsNewDialog(Version newVersion, string notes, string releaseUrl, ILogger logger)
    {
        InitializeComponent();
        _releaseUrl = releaseUrl;
        _logger = logger;

        TitleText.Text = $"Что нового в v{newVersion}";
        SubtitleText.Text = "Программа была обновлена. Вот что изменилось:";
        NotesText.Text = string.IsNullOrWhiteSpace(notes)
            ? "Описание изменений недоступно. Открой страницу релиза на GitHub для подробностей."
            : CleanMarkdown(notes);
    }

    private static string CleanMarkdown(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimStart();
            if (l.StartsWith("### "))      lines[i] = l.Substring(4);
            else if (l.StartsWith("## "))  lines[i] = l.Substring(3);
            else if (l.StartsWith("# "))   lines[i] = l.Substring(2);
            else if (l.StartsWith("- "))   lines[i] = "• " + l.Substring(2);
            else if (l.StartsWith("* "))   lines[i] = "• " + l.Substring(2);
        }
        return string.Join('\n', lines).Trim();
    }

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_releaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to open release URL in browser");
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
