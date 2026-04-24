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

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent header.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ErneyTranslateTool/{_currentVersion} (+https://github.com/erneywhite/erney-translate-tool)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var release = await http.GetFromJsonAsync<GitHubRelease>(LatestReleaseUrl, ct);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                _logger.Information("Update check: empty release info");
                return null;
            }

            var latest = ParseVersion(release.TagName);
            if (latest == null)
            {
                _logger.Information("Update check: could not parse tag {Tag}", release.TagName);
                return null;
            }

            _logger.Information("Update check: current={Current}, latest={Latest}",
                _currentVersion, latest);

            if (latest <= _currentVersion)
                return new UpdateInfo(false, _currentVersion, latest, release.HtmlUrl ?? "", release.Body ?? "");

            return new UpdateInfo(true, _currentVersion, latest, release.HtmlUrl ?? "", release.Body ?? "");
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Update check failed (this is fine — user has no internet, " +
                                    "GitHub rate-limited us, or the repo doesn't have releases yet)");
            return null;
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

public record UpdateInfo(bool IsNewer, Version Current, Version Latest, string ReleaseUrl, string Notes);
