using System.Text;
using System.Text.Json;
using Serilog;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Fire-and-forget usage telemetry: POSTs a hit to the ApplicationStats /stats
/// endpoint at application launch so usage can be tracked on the dashboard.
/// The endpoint is rate-limited to one call per hour per IP per application,
/// so additional launches are silently dropped server-side.
/// </summary>
internal sealed class StatsApiClient : IDisposable
{
    private const string ApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private readonly HttpClient _httpClient;
    private readonly string _applicationId;
    private readonly string _version;
    private int _failureReporting;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Creates a client with a 10-second HTTP timeout, a bearer API key, and the calling
    /// assembly's name and version for the usage hit.
    /// </summary>
    public StatsApiClient()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        var assembly = typeof(StatsApiClient).Assembly.GetName();
        _applicationId = assembly.Name ?? "rvzsharp-cli";
        _version = assembly.Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Asynchronously POSTs a usage hit to the stats endpoint. Best-effort: network failures
    /// and non-429 responses are logged at debug level and are never thrown to the caller.
    /// </summary>
    /// <returns>A task that completes when the usage hit has been handled.</returns>
    public async Task ReportUsageAsync()
    {
        try
        {
            var payload = new { applicationId = _applicationId, version = _version };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(ApiUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != (int)System.Net.HttpStatusCode.TooManyRequests)
            {
                ReportFailure($"Stats API returned HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ReportFailure($"Failed to report usage: {ex.Message}");
        }
    }

    /// <summary>Releases the underlying HttpClient.</summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>Logs a telemetry submission problem (at Debug level so it never
    /// triggers a bug report). Guarded so a failure while reporting a failure
    /// cannot recurse.</summary>
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
}