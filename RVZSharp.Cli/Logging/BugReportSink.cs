using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Serilog sink that forwards log events at or above a minimum level to the bug-report
/// API as fire-and-forget HTTP requests, without blocking the caller.
/// </summary>
internal sealed class BugReportSink : ILogEventSink
{
    private readonly BugReportApiClient _client;
    private readonly LogEventLevel _minLevel;
    private readonly ConcurrentQueue<Task> _pending = new();

    /// <summary>Creates a sink that reports events at or above <c>minLevel</c>.</summary>
    /// <param name="client">The API client used to send bug reports.</param>
    /// <param name="minLevel">The minimum event level that triggers a report (default: Warning).</param>
    public BugReportSink(BugReportApiClient client, LogEventLevel minLevel = LogEventLevel.Warning)
    {
        _client = client;
        _minLevel = minLevel;
    }

    /// <summary>
    /// Queues a bug report for each event at or above the minimum level. The HTTP send
    /// is fire-and-forget; await FlushAsync before the process exits to ensure delivery.
    /// </summary>
    /// <param name="logEvent">The Serilog event to evaluate and possibly report.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minLevel)
        {
            return;
        }

        var message = logEvent.RenderMessage();
        var exception = logEvent.Exception;
        _pending.Enqueue(_client.SendBugReportAsync(message, exception));
    }

    /// <summary>Awaits all submitted bug-report requests (fire-and-forget HTTP sends
    /// are otherwise dropped when the process exits or the client is disposed).</summary>
    public async Task FlushAsync()
    {
        var pending = new List<Task>();
        while (_pending.TryDequeue(out var task))
        {
            pending.Add(task);
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}