using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ErneyTranslateTool.Core.Updates;
using Serilog;

namespace ErneyTranslateTool.Views.Dialogs;

/// <summary>
/// Modal dialog shown when a newer release is detected on GitHub. Lets the
/// user read the release notes and either install in-place (download +
/// silent installer launch + app exit) or open the release page in the
/// browser as a fallback.
/// </summary>
public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateCheckResult _result;
    private readonly UpdateDownloader _downloader;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// True if the installer was successfully launched and the host app
    /// should now shut down to free the install folder.
    /// </summary>
    public bool ShouldExitForUpdate { get; private set; }

    public UpdateAvailableDialog(UpdateCheckResult result, UpdateDownloader downloader, ILogger logger)
    {
        InitializeComponent();
        _result = result;
        _downloader = downloader;
        _logger = logger;

        TitleText.Text = $"Доступна версия {result.Latest}";

        var sizeMb = result.InstallerSize > 0
            ? $" · {result.InstallerSize / 1024.0 / 1024.0:F0} МБ"
            : string.Empty;
        SubtitleText.Text = $"У тебя сейчас {result.Current}{sizeMb}";

        NotesText.Text = string.IsNullOrWhiteSpace(result.Notes)
            ? "Описание изменений не указано."
            : CleanMarkdown(result.Notes);

        // No installer asset attached → can only open the release page.
        if (string.IsNullOrEmpty(result.InstallerUrl))
        {
            InstallButton.IsEnabled = false;
            InstallButton.ToolTip = "К релизу не прикреплён установщик — открой страницу вручную.";
        }
    }

    /// <summary>
    /// Trim a few markdown niceties so plain-text rendering looks less ugly.
    /// We don't pull a markdown parser just for this — release notes are
    /// short and the user gets the gist either way.
    /// </summary>
    private static string CleanMarkdown(string md)
    {
        // Strip leading "## " / "# " from headings, leave the text.
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

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_result.ReleaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_result.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to open release URL in browser");
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_result.InstallerUrl)) return;

        // Switch UI into download mode: hide install/later, show progress + cancel.
        InstallButton.Visibility = Visibility.Collapsed;
        LaterButton.Visibility = Visibility.Collapsed;
        OpenPageButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Скачивание установщика…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            DownloadProgress.Value = p;
            ProgressPercent.Text = $"{p * 100:F0}%";
        });

        try
        {
            var installerPath = await _downloader.DownloadAsync(
                _result.InstallerUrl, progress, _cts.Token);

            ProgressLabel.Text = "Запуск установщика…";
            ProgressPercent.Text = "100%";
            DownloadProgress.Value = 1.0;

            // Tiny pause so the user sees the "100%" before the app closes.
            await Task.Delay(400);

            _downloader.LaunchSilent(installerPath);
            ShouldExitForUpdate = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ResetUiAfterFailure("Скачивание отменено.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download/launch update installer");
            ResetUiAfterFailure($"Не удалось скачать обновление:\n{ex.Message}");
        }
    }

    private void ResetUiAfterFailure(string message)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Visible;
        LaterButton.Visibility = Visibility.Visible;
        OpenPageButton.IsEnabled = true;

        MessageBox.Show(this, message, "Обновление",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
