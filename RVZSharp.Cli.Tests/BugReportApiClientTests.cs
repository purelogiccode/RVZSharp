using System.Net;
using System.Text.Json.Nodes;
using RVZSharp.Cli.Logging;
using RVZSharp.Cli.Tests.Helpers;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Verifies the bug-report HTTP client submits the required payload and never lets a
/// transport failure escape to the CLI.
/// </summary>
public class BugReportApiClientTests
{
    /// <summary>Verifies a successful POST carries the full report and exception details.</summary>
    [Fact]
    public async Task SendBugReportAsync_Success_PostsCompletePayload()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"id\":42}");
        using var client = new BugReportApiClient(new HttpClient(handler));
        var exception = new InvalidOperationException("boom");

        await client.SendBugReportAsync("Conversion failed", exception);

        var body = Assert.Single(handler.RequestBodies);
        var payload = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("RVZSharp.Cli", payload["applicationName"]!.GetValue<string>());
        Assert.Equal(CliVersion.Product, payload["version"]!.GetValue<string>());
        var message = payload["message"]!.GetValue<string>();
        Assert.Contains("=== Environment Details ===", message);
        Assert.Contains("=== Error Details ===", message);
        Assert.Contains("Conversion failed", message);
        Assert.Contains("=== Exception Details ===", message);
        Assert.Contains("Type: System.InvalidOperationException", message);
        Assert.Contains("Message: boom", message);
        Assert.Contains("boom", payload["stackTrace"]!.GetValue<string>());
    }

    /// <summary>Verifies oversized messages are truncated to the API's 4,000-character limit.</summary>
    [Fact]
    public async Task SendBugReportAsync_LongMessage_TruncatesPayload()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        using var client = new BugReportApiClient(new HttpClient(handler));

        await client.SendBugReportAsync(new string('x', 5000), null);

        var body = Assert.Single(handler.RequestBodies);
        var payload = JsonNode.Parse(body)!.AsObject();
        Assert.True(payload["message"]!.GetValue<string>().Length <= 4000);
    }

    /// <summary>Verifies a non-success response is handled without throwing.</summary>
    [Fact]
    public async Task SendBugReportAsync_NonSuccess_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError);
        using var client = new BugReportApiClient(new HttpClient(handler));

        await client.SendBugReportAsync("failed", null);

        Assert.Single(handler.Requests);
    }

    /// <summary>Verifies an HTTP transport exception is swallowed.</summary>
    [Fact]
    public async Task SendBugReportAsync_TransportFailure_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        using var client = new BugReportApiClient(new HttpClient(handler));

        await client.SendBugReportAsync("failed", null);
    }
}
