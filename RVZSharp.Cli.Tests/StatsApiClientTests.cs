using System.Net;
using System.Text.Json.Nodes;
using RVZSharp.Cli.Logging;
using RVZSharp.Cli.Tests.Helpers;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Verifies the launch-time ApplicationStats telemetry client is best-effort: every response
/// shape, including rate limiting and authentication errors, completes without throwing.
/// </summary>
public class StatsApiClientTests
{
    /// <summary>Verifies a successful hit posts the application id and version.</summary>
    [Fact]
    public async Task ReportUsageAsync_Success_PostsApplicationAndVersion()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"message\":\"Stats recorded successfully\"}");
        using var client = new StatsApiClient(new HttpClient(handler));

        await client.ReportUsageAsync();

        var body = Assert.Single(handler.RequestBodies);
        var payload = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("RVZSharp.Cli", payload["applicationId"]!.GetValue<string>());
        Assert.Equal(CliVersion.Product, payload["version"]!.GetValue<string>());
    }

    /// <summary>Verifies the hourly rate limit is treated as success and swallowed.</summary>
    [Fact]
    public async Task ReportUsageAsync_RateLimited_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var client = new StatsApiClient(new HttpClient(handler));

        await client.ReportUsageAsync();

        Assert.Single(handler.Requests);
    }

    /// <summary>Verifies a server error is swallowed.</summary>
    [Fact]
    public async Task ReportUsageAsync_ServerError_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError);
        using var client = new StatsApiClient(new HttpClient(handler));

        await client.ReportUsageAsync();
    }

    /// <summary>Verifies the endpoint's HTTP 200 error body (auth failure) is swallowed.</summary>
    [Fact]
    public async Task ReportUsageAsync_ErrorBody_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"error\":\"Unauthorized\"}");
        using var client = new StatsApiClient(new HttpClient(handler));

        await client.ReportUsageAsync();
    }

    /// <summary>Verifies an HTTP transport exception is swallowed.</summary>
    [Fact]
    public async Task ReportUsageAsync_TransportFailure_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        using var client = new StatsApiClient(new HttpClient(handler));

        await client.ReportUsageAsync();
    }
}
