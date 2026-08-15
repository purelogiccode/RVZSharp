using Serilog;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Configures the process-wide Serilog logger for the CLI: console output, a rolling
/// file under the temp directory, and a bug-report sink that forwards warnings and
/// errors to the bug-report API.
/// </summary>
internal static class LogSetup
{
    private static BugReportApiClient? _bugReportClient;
    private static BugReportSink? _bugReportSink;

    /// <summary>
    /// Creates the log directory and replaces Log.Logger with the CLI logging pipeline
    /// (console, daily rolling file, bug-report sink). Call once at startup.
    /// </summary>
    public static void Initialize()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "RVZSharp", "logs");
        Directory.CreateDirectory(logDir);

        _bugReportClient = new BugReportApiClient();
        _bugReportSink = new BugReportSink(_bugReportClient);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "rvzsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.Sink(_bugReportSink)
            .Enrich.WithProperty("Application", "RVZSharp")
            .CreateLogger();
    }

    /// <summary>
    /// Flushes and closes Serilog, waits up to 20 seconds for queued bug reports to reach
    /// the API, and disposes the bug-report client. Call once at shutdown.
    /// </summary>
    public static void Shutdown()
    {
        Log.CloseAndFlush();
        // Wait for the queued bug reports to reach the API before disposing the client.
        _bugReportSink?.FlushAsync().Wait(TimeSpan.FromSeconds(20));
        _bugReportClient?.Dispose();
    }
}