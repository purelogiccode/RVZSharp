using Serilog;

namespace RVZSharp.Cli.Logging;

internal static class LogSetup
{
    private static BugReportApiClient? _bugReportClient;

    public static void Initialize()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "RVZSharp", "logs");
        Directory.CreateDirectory(logDir);

        _bugReportClient = new BugReportApiClient("RVZSharp", "0.1.0");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDir, "rvzsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.Sink(new BugReportSink(_bugReportClient))
            .Enrich.WithProperty("Application", "RVZSharp")
            .CreateLogger();
    }

    public static void Shutdown()
    {
        Log.CloseAndFlush();
        _bugReportClient?.Dispose();
    }
}