using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace RVZSharp.Cli.Logging;

internal sealed class BugReportSink : ILogEventSink
{
    private readonly BugReportApiClient _client;
    private readonly LogEventLevel _minLevel;
    private readonly ConcurrentQueue<Task> _pending = new();

    public BugReportSink(BugReportApiClient client, LogEventLevel minLevel = LogEventLevel.Warning)
    {
        _client = client;
        _minLevel = minLevel;
    }

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