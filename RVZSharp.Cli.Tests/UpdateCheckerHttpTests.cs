using System.Net;
using RVZSharp.Cli.Tests.Helpers;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Verifies the GitHub update check end to end against a deterministic HTTP handler:
/// newer, equal, draft, prerelease, malformed and failing responses.
/// </summary>
public class UpdateCheckerHttpTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK");

    /// <summary>Clears the opt-out switch for the duration of each test.</summary>
    public UpdateCheckerHttpTests()
    {
        Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", null);
    }

    /// <summary>Restores the environment variable changed by the test.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", _original);
    }

    /// <summary>Verifies a newer, stable release is returned with its tag and URL.</summary>
    [Fact]
    public async Task CheckAsync_NewerRelease_ReturnsRelease()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v99.0.0"));

        using var checker = new UpdateChecker(new HttpClient(handler));
        var release = await checker.CheckAsync();

        Assert.NotNull(release);
        Assert.Equal("v99.0.0", release.Tag);
        Assert.Equal(new Version(99, 0, 0), release.Version);
        Assert.Equal("https://example.test/release", release.Url);
    }

    /// <summary>Verifies the running version is not reported as an update.</summary>
    [Fact]
    public async Task CheckAsync_SameVersion_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v" + CliVersion.Product));

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies draft releases are ignored.</summary>
    [Fact]
    public async Task CheckAsync_Draft_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v99.0.0", draft: true));

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies prerelease releases are ignored.</summary>
    [Fact]
    public async Task CheckAsync_Prerelease_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v99.0.0", prerelease: true));

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies a non-success GitHub response is silent.</summary>
    [Fact]
    public async Task CheckAsync_HttpError_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound);

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies malformed JSON is silent.</summary>
    [Fact]
    public async Task CheckAsync_InvalidJson_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "not-json");

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies a transport failure is silent.</summary>
    [Fact]
    public async Task CheckAsync_TransportFailure_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));

        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
    }

    /// <summary>Verifies the opt-out switch skips the HTTP request entirely.</summary>
    [Fact]
    public async Task CheckAsync_Disabled_DoesNotSendRequest()
    {
        Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", "1");
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v99.0.0"));
        using var checker = new UpdateChecker(new HttpClient(handler));

        Assert.Null(await checker.CheckAsync());
        Assert.Empty(handler.Requests);
    }

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false)
    {
        return $$"""
                 {
                   "tag_name": "{{tag}}",
                   "html_url": "https://example.test/release",
                   "draft": {{draft.ToString().ToLowerInvariant()}},
                   "prerelease": {{prerelease.ToString().ToLowerInvariant()}}
                 }
                 """;
    }
}
