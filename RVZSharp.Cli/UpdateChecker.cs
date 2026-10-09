using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Serilog;

namespace RVZSharp.Cli;

/// <summary>
/// Best-effort update check against the project's GitHub releases. At launch the CLI asks
/// the GitHub API for the latest release; after the command finishes it tells the user when
/// a newer version exists and offers to open the release page. The check is silent on any
/// failure and can be disabled with the <c>RVZSHARP_NO_UPDATE_CHECK</c> environment variable.
/// </summary>
internal sealed class UpdateChecker : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/purelogiccode/RVZSharp/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/purelogiccode/RVZSharp/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private int _failureReporting;

    /// <summary>
    /// Creates a checker with a 10-second timeout, the GitHub API headers and the running
    /// build's product version (the informational version, without build metadata).
    /// </summary>
    public UpdateChecker()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RVZSharp.Cli", CurrentVersionText()));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _currentVersion = CurrentVersion();
    }

    /// <summary>True when the user disabled the update check via <c>RVZSHARP_NO_UPDATE_CHECK</c>.</summary>
    public static bool IsDisabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK"));

    /// <summary>
    /// Fetches the latest GitHub release and returns it when it is newer than the running
    /// build. Returns null when there is no update, the check is disabled, or anything fails.
    /// </summary>
    /// <returns>The newer release, or null.</returns>
    public async Task<ReleaseInfo?> CheckAsync()
    {
        if (IsDisabled)
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ReportFailure($"Update check returned HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (JsonNode.Parse(body) is not JsonObject json)
            {
                return null;
            }

            if (json["draft"]?.GetValue<bool>() == true ||
                json["prerelease"]?.GetValue<bool>() == true)
            {
                return null;
            }

            var tag = json["tag_name"]?.GetValue<string>();
            var version = ParseTag(tag);
            if (version is null || version <= _currentVersion)
            {
                Log.Information(
                    "Update check: no update available (current {Current}, latest {Latest})",
                    _currentVersion, tag);
                return null;
            }

            var url = json["html_url"]?.GetValue<string>() ?? ReleasesPageUrl;
            Log.Information(
                "Update check: update available {Latest} (current {Current})", tag, _currentVersion);
            return new ReleaseInfo(tag!, version, url);
        }
        catch (Exception e)
        {
            ReportFailure($"Update check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Waits briefly for <see cref="CheckAsync"/> and, when a newer release exists and the
    /// console is interactive, prints a notice and asks whether to open the release page.
    /// Non-interactive runs (redirected input or error output) never prompt.
    /// </summary>
    /// <param name="check">The task returned by <see cref="CheckAsync"/> at launch.</param>
    public void NotifyIfAvailable(Task<ReleaseInfo?> check)
    {
        if (IsDisabled || Console.IsInputRedirected || Console.IsErrorRedirected)
        {
            return;
        }

        ReleaseInfo? release;
        try
        {
            release = check.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Debug("Update check did not finish: {Message}", e.Message);
            return;
        }

        if (release is null)
        {
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"A new version of RVZSharp.Cli is available: {release.Tag} (you have {_currentVersion}).");
        Console.Error.WriteLine($"Release page: {release.Url}");
        Console.Error.Write("Open the release page in your browser? [y/N] ");

        string? answer;
        try
        {
            answer = Console.ReadLine();
        }
        catch (IOException)
        {
            return;
        }

        if (answer is not null &&
            answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(release.Url);
        }
    }

    /// <summary>Releases the underlying HttpClient.</summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>The running build's product version (shared with telemetry and bug reports).</summary>
    private static Version CurrentVersion()
    {
        return CliVersion.Parsed;
    }

    private static string CurrentVersionText()
    {
        return CliVersion.Product;
    }

    /// <summary>Parses a release tag like <c>v1.2.3</c> or <c>1.2.3</c> (build metadata ignored).</summary>
    /// <param name="tag">The tag text, with or without a leading 'v'.</param>
    /// <returns>The parsed version, or null when the tag is not a version.</returns>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>Opens <paramref name="url"/> in the user's default browser (best-effort).</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception e)
        {
            Log.Debug("Could not open {Url}: {Message}", url, e.Message);
        }
    }

    /// <summary>Logs an update-check problem at debug level. Guarded so a failure while
    /// reporting a failure cannot recurse.</summary>
    private void ReportFailure(string detail)
    {
        if (Interlocked.Exchange(ref _failureReporting, 1) != 0)
        {
            return;
        }

        try
        {
            Log.Debug("{Detail}", detail);
        }
        finally
        {
            Interlocked.Exchange(ref _failureReporting, 0);
        }
    }

    /// <summary>A GitHub release that is newer than the running build.</summary>
    /// <param name="Tag">The release tag, e.g. <c>v1.2.3</c>.</param>
    /// <param name="Version">The parsed version.</param>
    /// <param name="Url">The release page URL.</param>
    internal sealed record ReleaseInfo(string Tag, Version Version, string Url);
}
