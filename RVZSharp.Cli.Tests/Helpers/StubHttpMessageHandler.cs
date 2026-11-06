using System.Collections.Concurrent;
using System.Net;

namespace RVZSharp.Cli.Tests.Helpers;

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> for the CLI logging and update-check tests:
/// records every request and body, and returns a caller-supplied response.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    /// <summary>Creates a handler that responds with <paramref name="respond"/>.</summary>
    /// <param name="respond">Builds the response for a request.</param>
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>Creates a handler that always returns the same status and body.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The response body.</param>
    public StubHttpMessageHandler(HttpStatusCode status, string body = "")
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        })
    {
    }

    /// <summary>Every request handled so far.</summary>
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The body text of every request handled so far.</summary>
    public ConcurrentQueue<string> RequestBodies { get; } = new();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        RequestBodies.Enqueue(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return _respond(request);
    }
}
