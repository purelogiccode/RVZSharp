using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RVZSharp.Cli.Logging;

internal sealed class BugReportApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _applicationName;
    private readonly string _version;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BugReportApiClient(string applicationName, string version)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e");
        _applicationName = applicationName;
        _version = version;
    }

    public async Task SendBugReportAsync(string errorMessage, string? stackTrace = null)
    {
        try
        {
            var envDetails = BuildEnvironmentDetails();
            var message = $"""
                === Environment Details ===
                {envDetails}
                === Error Details ===
                {errorMessage}
                """;

            var payload = new
            {
                message,
                applicationName = _applicationName,
                version = _version,
                environment = RuntimeInformation.OSDescription,
                stackTrace
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(
                "https://www.purelogiccode.com/bugreport/api/send-bug-report", content).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"Bug report API returned {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to send bug report: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private static string BuildEnvironmentDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET Version: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"CLR Version: {Environment.Version}");
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine($"Machine Name: {Environment.MachineName}");
        sb.AppendLine($"User Name: {Environment.UserName}");
        sb.AppendLine($"Working Directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"Command Line: {Environment.CommandLine}");
        sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine($"System Page Size: {Environment.SystemPageSize}");
        sb.AppendLine($"Tick Count (ms): {Environment.TickCount}");
        sb.AppendLine($"User Interactive: {Environment.UserInteractive}");

        return sb.ToString();
    }
}