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

        // Re-initializing (hosting, tests) must close the old pipeline before its HTTP
        // transport is disposed, so queued reports still have a client to use.
        if (_bugReportClient is not null)
        {
            Log.CloseAndFlush();
            _bugReportClient.Dispose();
        }

        _bugReportClient = new BugReportApiClient();
        // Forward every warning, error and fatal event to the bug-report API.
        _bugReportSink = new BugReportSink(_bugReportClient);

        var logDir = Path.Combine(Path.GetTempPath(), "RVZSharp", "logs");
        Log.Logger = CreateLogger(logDir, includeFile: true);
    }

    /// <summary>
    /// Builds the CLI logger. The daily rolling file is best-effort: an unavailable or
    /// read-only temp directory must not stop the CLI from running, so a failed file sink
    /// falls back to console + bug reports and finally to a silent logger.
    /// </summary>
    private static ILogger CreateLogger(string logDir, bool includeFile)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
            .WriteTo.Sink(_bugReportSink!)
            .Enrich.WithProperty("Application", "RVZSharp");

        if (includeFile)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                configuration = configuration.WriteTo.File(
                    Path.Combine(logDir, "rvzsharp-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14);
            }
            catch (Exception e) when (IsLogFileError(e))
            {
                // Retry without the file sink; console and bug reports stay active.
                return CreateLogger(logDir, includeFile: false);
            }
        }

        try
        {
            return configuration.CreateLogger();
        }
        catch (Exception e) when (includeFile && IsLogFileError(e))
        {
            return CreateLogger(logDir, includeFile: false);
        }
        catch (Exception e) when (!includeFile && IsLogFileError(e))
        {
            // Last resort: never let diagnostics prevent the command from running.
            return new LoggerConfiguration().CreateLogger();
        }
    }

    private static bool IsLogFileError(Exception e)
    {
        return e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException;
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
