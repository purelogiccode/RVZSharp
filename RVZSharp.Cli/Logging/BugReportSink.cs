using Serilog.Core;
using Serilog.Events;

namespace RVZSharp.Cli.Logging;

internal sealed class BugReportSink : ILogEventSink
{
    private readonly BugReportApiClient _client;
    private readonly LogEventLevel _minLevel;

    public BugReportSink(BugReportApiClient client, LogEventLevel minLevel = LogEventLevel.Warning)
    {
        _client = client;
        _minLevel = minLevel;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minLevel)
            return;

        var message = logEvent.RenderMessage();
        var stackTrace = logEvent.Exception?.ToString();
        _ = _client.SendBugReportAsync(message, stackTrace);
    }
}