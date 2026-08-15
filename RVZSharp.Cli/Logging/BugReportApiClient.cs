using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Serilog;

namespace RVZSharp.Cli.Logging;

internal sealed class BugReportApiClient : IDisposable
{
    private const string ApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const int MaxMessageLength = 4000;
    private const int MaxStackTraceLength = 8000;
    private readonly HttpClient _httpClient;
    private readonly string _applicationName;
    private readonly string _version;
    private int _failureReporting;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BugReportApiClient()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", ApiKey);
        var assembly = typeof(BugReportApiClient).Assembly.GetName();
        _applicationName = assembly.Name ?? "RVZSharp";
        _version = assembly.Version?.ToString(3) ?? "0.0.0";
    }

    public async Task SendBugReportAsync(string errorMessage, Exception? exception)
    {
        try
        {
            var details = BuildBugReportDetails(errorMessage, exception);
            var payload = new
            {
                message = Truncate(details, MaxMessageLength),
                applicationName = _applicationName,
                version = _version,
                environment = RuntimeInformation.OSDescription,
                stackTrace = Truncate(exception?.ToString(), MaxStackTraceLength)
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
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

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>Logs a bug-report submission problem. Guarded so a failure while
    /// reporting a failure cannot recurse (the API being down would otherwise produce
    /// an endless warning -> report -> warning loop).</summary>
    private void ReportFailure(string detail)
    {
        if (Interlocked.Exchange(ref _failureReporting, 1) != 0)
        {
            return;
        }

        try
        {
            Log.Warning("{Detail}", detail);
        }
        finally
        {
            Interlocked.Exchange(ref _failureReporting, 0);
        }
    }

    private string BuildBugReportDetails(string errorMessage, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine("Date: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine($"Application Name: {_applicationName}");
        sb.AppendLine($"Application Version: {_version}");
        sb.AppendLine($"OS Version: {Environment.OSVersion}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Bitness: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        sb.AppendLine($"Windows Version: {GetWindowsVersion()}");
        sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine($"Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine($"Temp Path: {Path.GetTempPath()}");
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(errorMessage);
        sb.AppendLine();
        sb.AppendLine("=== Exception Details ===");
        if (exception is null)
        {
            sb.AppendLine("Type: (none)");
            sb.AppendLine("Message: (none)");
            sb.AppendLine("Source: (none)");
            sb.AppendLine("StackTrace: (none)");
        }
        else
        {
            sb.AppendLine($"Type: {exception.GetType().FullName}");
            sb.AppendLine($"Message: {exception.Message}");
            sb.AppendLine($"Source: {exception.Source}");
            sb.AppendLine($"StackTrace: {exception.StackTrace}");
        }

        return sb.ToString();
    }

    private static string GetWindowsVersion()
    {
        return OperatingSystem.IsWindows()
            ? $"{Environment.OSVersion} ({RuntimeInformation.OSDescription})"
            : RuntimeInformation.OSDescription;
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