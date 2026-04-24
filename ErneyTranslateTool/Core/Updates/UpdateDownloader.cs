using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Updates;

/// <summary>
/// Downloads the new installer to %TEMP% and launches it silently.
/// </summary>
/// <remarks>
/// %TEMP% is preferred over the install folder because Windows Defender's
/// realtime scan briefly locks freshly-written .exe files in the install dir,
/// which can race with the installer's own file replacement step. Temp is
/// excluded from realtime scan by default.
/// </remarks>
public class UpdateDownloader
{
    private const int BufferSize = 1 << 16; // 64 KB — good throughput, low overhead.

    private readonly ILogger _logger;

    public UpdateDownloader(ILogger logger) => _logger = logger;

    /// <summary>
    /// Stream the installer .exe to a temp file. Reports progress as a fraction
    /// in [0, 1]. Throws on network/IO errors so callers can show a friendly
    /// "не удалось скачать" instead of swallowing.
    /// </summary>
    public async Task<string> DownloadAsync(string url, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Installer URL is empty", nameof(url));

        var dir = Path.Combine(Path.GetTempPath(), "ErneyTranslateTool-Update");
        Directory.CreateDirectory(dir);

        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(fileName)) fileName = "ErneyTranslateTool-Setup.exe";

        var dst = Path.Combine(dir, fileName);
        var tmp = dst + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ErneyTranslateTool-Updater (+https://github.com/erneywhite/erney-translate-tool)");

        _logger.Information("Downloading update installer from {Url}", url);

        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0L;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(tmp);

            var buffer = new byte[BufferSize];
            long copied = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                if (total > 0)
                    progress?.Report((double)copied / total);
            }
        }

        if (File.Exists(dst)) File.Delete(dst);
        File.Move(tmp, dst);

        _logger.Information("Update installer downloaded to {Path} ({Bytes} bytes)",
            dst, new FileInfo(dst).Length);

        return dst;
    }

    /// <summary>
    /// Launch the downloaded installer silently and return — caller should
    /// shut the app down right after so the installer can replace files.
    /// /SILENT keeps a small progress window visible (less spooky than total
    /// silence). /SUPPRESSMSGBOXES skips any "are you sure" prompts.
    /// /CLOSEAPPLICATIONS + /RESTARTAPPLICATIONS let Restart Manager close the
    /// running exe and relaunch it after install.
    /// </summary>
    public void LaunchSilent(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer not found", installerPath);

        var args = "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART";
        _logger.Information("Launching installer: {Path} {Args}", installerPath, args);

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = args,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(),
        });
    }
}
