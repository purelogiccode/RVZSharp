namespace RVZSharp.Cli.Logging;

/// <summary>
/// Transport abstraction used by <see cref="BugReportSink"/> to submit a bug report.
/// Keeping the sink behind this interface lets the logging pipeline be unit tested
/// without making network calls.
/// </summary>
internal interface IBugReportSender
{
    /// <summary>
    /// Asynchronously sends a bug report. Implementations must be best-effort and must not
    /// throw: a failing bug-report transport may never take down the CLI.
    /// </summary>
    /// <param name="errorMessage">The rendered error message to report.</param>
    /// <param name="exception">The exception whose details and stack trace to include, or null.</param>
    /// <returns>A task that completes when the report request has been handled.</returns>
    Task SendBugReportAsync(string errorMessage, Exception? exception);
}
