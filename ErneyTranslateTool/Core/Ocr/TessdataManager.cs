using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// Owns the user's tessdata directory: copies bundled .traineddata files
/// from the install dir on first run, lists what's installed, and downloads
/// new ones from the official tessdata_fast GitHub mirror.
/// </summary>
public class TessdataManager
{
    private const string FastBaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";
    private const string BestBaseUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";

    private readonly ILogger _logger;
    public string TessdataPath { get; }

    public TessdataManager(string appDataPath, ILogger logger)
    {
        _logger = logger;
        TessdataPath = Path.Combine(appDataPath, "tessdata");
        Directory.CreateDirectory(TessdataPath);
        SeedFromBundled();
    }

    /// <summary>
    /// Copy bundled tessdata files into the user dir, refreshing any installed
    /// file whose size differs from the bundled one (e.g. fast → best upgrade
    /// after an app update).
    /// </summary>
    private void SeedFromBundled()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var bundledDir = Path.Combine(baseDir, "tessdata");
            if (!Directory.Exists(bundledDir)) return;

            foreach (var src in Directory.GetFiles(bundledDir, "*.traineddata"))
            {
                var dst = Path.Combine(TessdataPath, Path.GetFileName(src));
                var srcLen = new FileInfo(src).Length;
                var needCopy = !File.Exists(dst) || new FileInfo(dst).Length != srcLen;
                if (needCopy)
                {
                    File.Copy(src, dst, overwrite: true);
                    _logger.Information("Seeded tessdata: {File} ({Bytes} bytes)",
                        Path.GetFileName(src), srcLen);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to seed bundled tessdata");
        }
    }

    public List<string> GetInstalledLanguageCodes()
    {
        if (!Directory.Exists(TessdataPath)) return new List<string>();
        return Directory.GetFiles(TessdataPath, "*.traineddata")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(c => !string.IsNullOrEmpty(c))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsInstalled(string code) =>
        File.Exists(Path.Combine(TessdataPath, code + ".traineddata"));

    public Task DownloadLanguageAsync(string code, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        DownloadLanguageAsync(code, useBestModel: false, progress, ct);

    public async Task DownloadLanguageAsync(string code, bool useBestModel,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var baseUrl = useBestModel ? BestBaseUrl : FastBaseUrl;
        var url = baseUrl + code + ".traineddata";
        var dst = Path.Combine(TessdataPath, code + ".traineddata");
        var tmp = dst + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var fs = File.Create(tmp))
        {
            var buffer = new byte[81920];
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
        _logger.Information("Downloaded tessdata: {Code} ({Bytes} bytes, {Quality})",
            code, total, useBestModel ? "best" : "fast");
    }

    public bool DeleteLanguage(string code)
    {
        try
        {
            var path = Path.Combine(TessdataPath, code + ".traineddata");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            _logger.Information("Deleted tessdata: {Code}", code);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete tessdata: {Code}", code);
            return false;
        }
    }
}
