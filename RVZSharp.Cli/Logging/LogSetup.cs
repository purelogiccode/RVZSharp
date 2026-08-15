using Serilog;

namespace RVZSharp.Cli.Logging;

internal static class LogSetup
{
    private static BugReportApiClient? _bugReportClient;
    private static BugReportSink? _bugReportSink;

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

    public static void Shutdown()
    {
        Log.CloseAndFlush();
        // Wait for the queued bug reports to reach the API before disposing the client.
        _bugReportSink?.FlushAsync().Wait(TimeSpan.FromSeconds(20));
        _bugReportClient?.Dispose();
    }
}