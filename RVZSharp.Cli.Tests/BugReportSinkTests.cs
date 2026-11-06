using System.Collections.Concurrent;
using RVZSharp.Cli.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Verifies the Serilog bug-report sink forwards exactly the warning-and-above events to the
/// bug-report transport, as required by the CLI logging policy.
/// </summary>
public class BugReportSinkTests
{
    /// <summary>Verifies the default minimum level and that warning events are submitted.</summary>
    [Fact]
    public async Task Emit_Warning_IsForwarded()
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender);

        sink.Emit(CreateEvent(LogEventLevel.Warning));
        await sink.FlushAsync();

        var report = Assert.Single(sender.Reports);
        Assert.Equal("Test message", report.Message);
        Assert.Null(report.Exception);
    }

    /// <summary>Verifies information and debug events are never forwarded.</summary>
    [Theory]
    [InlineData(LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    public async Task Emit_BelowWarning_IsIgnored(LogEventLevel level)
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender);

        sink.Emit(CreateEvent(level));
        await sink.FlushAsync();

        Assert.Empty(sender.Reports);
    }

    /// <summary>Verifies errors carry their exception into the report.</summary>
    [Fact]
    public async Task Emit_Error_ForwardsException()
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender);
        var exception = new InvalidOperationException("boom");

        sink.Emit(CreateEvent(LogEventLevel.Error, exception));
        await sink.FlushAsync();

        var report = Assert.Single(sender.Reports);
        Assert.Same(exception, report.Exception);
        Assert.Equal("Test message", report.Message);
    }

    /// <summary>Verifies fatal events are forwarded too.</summary>
    [Fact]
    public async Task Emit_Fatal_IsForwarded()
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender);

        sink.Emit(CreateEvent(LogEventLevel.Fatal));
        await sink.FlushAsync();

        Assert.Single(sender.Reports);
    }

    /// <summary>Verifies the configurable minimum level can raise the threshold to errors.</summary>
    [Fact]
    public async Task Emit_CustomMinimum_AppliesThreshold()
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender, LogEventLevel.Error);

        sink.Emit(CreateEvent(LogEventLevel.Warning));
        sink.Emit(CreateEvent(LogEventLevel.Error));
        await sink.FlushAsync();

        Assert.Single(sender.Reports);
    }

    /// <summary>Verifies <see cref="BugReportSink.HasPending"/> reflects queued reports.</summary>
    [Fact]
    public async Task HasPending_IsTrueUntilFlushed()
    {
        var sender = new RecordingSender();
        var sink = new BugReportSink(sender);

        sink.Emit(CreateEvent(LogEventLevel.Warning));
        Assert.True(sink.HasPending);
        await sink.FlushAsync();

        Assert.False(sink.HasPending);
    }

    /// <summary>Verifies a faulted transport cannot break shutdown.</summary>
    [Fact]
    public async Task FlushAsync_FaultedSender_DoesNotThrow()
    {
        var sink = new BugReportSink(new FaultingSender());

        sink.Emit(CreateEvent(LogEventLevel.Error));
        await sink.FlushAsync();

        Assert.False(sink.HasPending);
    }

    /// <summary>Verifies a transport that throws synchronously cannot break logging.</summary>
    [Fact]
    public void Emit_ThrowingSender_DoesNotThrow()
    {
        var sink = new BugReportSink(new ThrowingSender());

        sink.Emit(CreateEvent(LogEventLevel.Error));
    }

    private static LogEvent CreateEvent(LogEventLevel level, Exception? exception = null)
    {
        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception,
            new MessageTemplateParser().Parse("Test message"),
            []);
    }

    private sealed class RecordingSender : IBugReportSender
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Reports { get; } = new();

        public Task SendBugReportAsync(string errorMessage, Exception? exception)
        {
            Reports.Enqueue((errorMessage, exception));
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingSender : IBugReportSender
    {
        public Task SendBugReportAsync(string errorMessage, Exception? exception)
        {
            return Task.FromException(new InvalidOperationException("transport down"));
        }
    }

    private sealed class ThrowingSender : IBugReportSender
    {
        public Task SendBugReportAsync(string errorMessage, Exception? exception)
        {
            throw new InvalidOperationException("transport unavailable");
        }
    }
}
