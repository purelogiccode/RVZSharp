using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Posts error details to the RVZSharp bug-report API, capturing environment and
/// exception information with bounded message and stack-trace lengths. Submission
/// failures are logged but never thrown.
/// </summary>
internal sealed class BugReportApiClient : IBugReportSender, IDisposable
{
    private const string ApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const int MaxMessageLength = 4000;
    private const int MaxStackTraceLength = 8000;
    private readonly HttpClient _httpClient;
    private readonly string _applicationName;
    private readonly string _version;
    private int _failureReporting;

    /// <summary>
    /// Creates a client with a 15-second HTTP timeout, the API key header, and the
    /// calling assembly's name and version for the report.
    /// </summary>
    public BugReportApiClient()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
    {
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", ApiKey);
    }

    /// <summary>
    /// Creates a client around an existing <see cref="HttpClient"/>. Used by the tests to
    /// exercise the request and response handling without network access.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    internal BugReportApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        var assembly = typeof(BugReportApiClient).Assembly.GetName();
        _applicationName = assembly.Name ?? "RVZSharp";
        _version = CliVersion.Product;
    }

    /// <summary>
    /// Asynchronously submits a bug report to the API. Best-effort: network failures and
    /// non-success responses are logged via ReportFailure and are never thrown to the caller.
    /// </summary>
    /// <param name="errorMessage">The rendered error message to report.</param>
    /// <param name="exception">The exception whose details and stack trace to include, or null.</param>
    /// <returns>A task that completes when the report request has been handled.</returns>
    public async Task SendBugReportAsync(string errorMessage, Exception? exception)
    {
        try
        {
            var details = BugReportDetails.Build(errorMessage, exception, _applicationName, _version);
            // JsonObject (not reflection-based serialization) keeps the CLI trim/AOT-safe.
            var payload = new JsonObject
            {
                ["message"] = Truncate(details, MaxMessageLength),
                ["applicationName"] = _applicationName,
                ["version"] = _version,
                ["environment"] = RuntimeInformation.OSDescription,
                ["stackTrace"] = Truncate(exception?.ToString(), MaxStackTraceLength)
            };

            var json = payload.ToJsonString();
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(ApiUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ReportFailure($"Bug report API returned HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ReportFailure($"Failed to submit bug report: {ex.Message}");
        }
    }

    /// <summary>Releases the underlying HttpClient.</summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>Logs a bug-report submission problem at Information level, below the sink's
    /// Warning threshold, so a failing API cannot recurse (warning -> report -> warning).
    /// The guard also prevents nested submission failures from reporting each other.</summary>
    private void ReportFailure(string detail)
    {
        if (Interlocked.Exchange(ref _failureReporting, 1) != 0)
        {
            return;
        }

        try
        {
            Log.Information("{Detail}", detail);
        }
        finally
        {
            Interlocked.Exchange(ref _failureReporting, 0);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
