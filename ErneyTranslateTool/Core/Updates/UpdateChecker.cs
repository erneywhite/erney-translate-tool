using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ErneyTranslateTool.Core.Updates;

/// <summary>
/// Checks GitHub Releases for a newer version of the app. Failed checks
/// (no network, rate limit, malformed response) are silent — we don't
/// want to nag the user with errors about an optional feature.
/// </summary>
public class UpdateChecker
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/erneywhite/erney-translate-tool/releases/latest";

    private readonly ILogger _logger;
    private readonly Version _currentVersion;

    public UpdateChecker(ILogger logger)
    {
        _logger = logger;
        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    }

    public Version CurrentVersion => _currentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent header.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ErneyTranslateTool/{_currentVersion} (+https://github.com/erneywhite/erney-translate-tool)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(LatestReleaseUrl, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Repo exists but has no published releases yet — that's not
                // an error, just "nothing to update to".
                _logger.Information("Update check: GitHub returned 404 (no releases published yet)");
                return UpdateCheckResult.NoReleases(_currentVersion);
            }

            resp.EnsureSuccessStatusCode();
            var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                _logger.Information("Update check: empty release info");
                return UpdateCheckResult.Error("Сервер вернул пустой ответ");
            }

            var latest = ParseVersion(release.TagName);
            if (latest == null)
            {
                _logger.Information("Update check: could not parse tag {Tag}", release.TagName);
                return UpdateCheckResult.Error($"Не удалось разобрать версию «{release.TagName}»");
            }

            _logger.Information("Update check: current={Current}, latest={Latest}",
                _currentVersion, latest);

            return latest > _currentVersion
                ? UpdateCheckResult.UpdateAvailable(_currentVersion, latest, release.HtmlUrl ?? "", release.Body ?? "")
                : UpdateCheckResult.UpToDate(_currentVersion, latest);
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Update check failed (network / rate limit / parse error)");
            return UpdateCheckResult.Error(ex.Message);
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        // Drop any pre-release suffix after '-': "1.2.3-rc1" -> "1.2.3"
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0) s = s.Substring(0, dashIdx);
        return Version.TryParse(s, out var v) ? v : null;
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}

public enum UpdateCheckOutcome { UpToDate, UpdateAvailable, NoReleases, Error }

public record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    Version Current,
    Version? Latest,
    string ReleaseUrl,
    string Notes,
    string ErrorMessage)
{
    public static UpdateCheckResult UpToDate(Version current, Version latest) =>
        new(UpdateCheckOutcome.UpToDate, current, latest, "", "", "");

    public static UpdateCheckResult UpdateAvailable(Version current, Version latest, string url, string notes) =>
        new(UpdateCheckOutcome.UpdateAvailable, current, latest, url, notes, "");

    public static UpdateCheckResult NoReleases(Version current) =>
        new(UpdateCheckOutcome.NoReleases, current, null, "", "", "");

    public static UpdateCheckResult Error(string message) =>
        new(UpdateCheckOutcome.Error, new Version(0, 0), null, "", "", message);
}
