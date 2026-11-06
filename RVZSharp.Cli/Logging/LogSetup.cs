using Serilog;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Configures the process-wide Serilog logger for the CLI: console output, a rolling
/// file under the temp directory, and a bug-report sink that forwards every warning,
/// error and fatal event to the bug-report API. Expected user and environment failures
/// are logged at Information so they never become bug reports. Console logs go to stderr
/// so stdout only ever carries the command's result (JSON, hashes, disc images written
/// with <c>-o -</c>).
/// </summary>
internal static class LogSetup
{
    private static BugReportApiClient? _bugReportClient;
    private static BugReportSink? _bugReportSink;
    private static int _shutdown;

    /// <summary>
    /// Creates the log directory and replaces Log.Logger with the CLI logging pipeline
    /// (console, daily rolling file, bug-report sink). Call once at startup.
    /// </summary>
    public static void Initialize()
    {
        Interlocked.Exchange(ref _shutdown, 0);
        var logDir = Path.Combine(Path.GetTempPath(), "RVZSharp", "logs");
        Directory.CreateDirectory(logDir);

        _bugReportClient = new BugReportApiClient();
        // Forward every warning, error and fatal event to the bug-report API.
        _bugReportSink = new BugReportSink(_bugReportClient, Serilog.Events.LogEventLevel.Warning);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
            .WriteTo.File(
                Path.Combine(logDir, "rvzsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.Sink(_bugReportSink)
            .Enrich.WithProperty("Application", "RVZSharp")
            .CreateLogger();
    }

    /// <summary>
    /// Flushes and closes Serilog, waits up to 5 seconds for queued bug reports to reach
    /// the API (only when something is actually queued), and disposes the bug-report client.
    /// Call once at shutdown.
    /// </summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        Log.CloseAndFlush();
        // Wait for the queued bug reports to reach the API before disposing the client, but
        // only when there is something to wait for: best-effort telemetry must not hold a
        // finished command open.
        if (_bugReportSink?.HasPending == true)
        {
            _bugReportSink.FlushAsync().Wait(TimeSpan.FromSeconds(5));
        }

        _bugReportClient?.Dispose();
    }
}
