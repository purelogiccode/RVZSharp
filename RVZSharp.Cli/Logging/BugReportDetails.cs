using System.Runtime.InteropServices;
using System.Text;

namespace RVZSharp.Cli.Logging;

/// <summary>
/// Builds the plain-text payload submitted to the bug-report API. The payload always contains
/// the three sections required by the bug-report service: environment, error and exception
/// details. Keeping the formatter separate from the HTTP client makes the required layout
/// directly unit-testable.
/// </summary>
internal static class BugReportDetails
{
    // The bug-report API accepts at most 4,000 characters in the message field. Bound every
    // variable-length field so a huge exception can never truncate away the required section
    // headers or the exception details.
    private const int MaxErrorMessageLength = 600;
    private const int MaxExceptionTypeLength = 150;
    private const int MaxExceptionMessageLength = 600;
    private const int MaxSourceLength = 150;
    private const int MaxStackTraceLength = 900;

    /// <summary>
    /// Formats a complete bug report.
    /// </summary>
    /// <param name="errorMessage">The error message to report.</param>
    /// <param name="exception">The exception to describe, or null when reporting a message only.</param>
    /// <param name="applicationName">The application name shown in the report.</param>
    /// <param name="version">The application version shown in the report.</param>
    /// <param name="timestamp">The report timestamp; defaults to the current local time.</param>
    /// <returns>The formatted report, with the environment, error and exception sections.</returns>
    internal static string Build(
        string errorMessage,
        Exception? exception,
        string applicationName,
        string version,
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var sb = new StringBuilder();
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine("Date: " + now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine($"Application Name: {applicationName}");
        sb.AppendLine($"Application Version: {version}");
        sb.AppendLine($"OS Version: {Truncate(Environment.OSVersion.ToString(), 200)}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Bitness: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        sb.AppendLine($"{PlatformLabel()} Version: {Truncate(RuntimeInformation.OSDescription, 200)}");
        sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine($"Base Directory: {Truncate(AppContext.BaseDirectory, 260)}");
        sb.AppendLine($"Temp Path: {Truncate(Path.GetTempPath(), 260)}");
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(Truncate(errorMessage, MaxErrorMessageLength));
        sb.AppendLine();
        sb.AppendLine("=== Exception Details ===");
        if (exception is null)
        {
            sb.AppendLine("Type: (none)");
            sb.AppendLine("Message: (none)");
            sb.AppendLine("Source: (none)");
            sb.AppendLine("StackTrace: (none)");
        }
        else
        {
            sb.AppendLine($"Type: {Truncate(exception.GetType().FullName, MaxExceptionTypeLength)}");
            sb.AppendLine($"Message: {Truncate(exception.Message, MaxExceptionMessageLength)}");
            sb.AppendLine($"Source: {Truncate(exception.Source, MaxSourceLength)}");
            sb.AppendLine($"StackTrace: {Truncate(exception.StackTrace, MaxStackTraceLength)}");
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "…";
    }

    /// <summary>
    /// The operating-system family label used before the runtime's OS description line.
    /// </summary>
    /// <returns><c>Windows</c>, <c>Linux</c>, <c>MacOSX</c> or <c>OS</c>.</returns>
    internal static string PlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "MacOSX";
        }

        return OperatingSystem.IsLinux() ? "Linux" : "OS";
    }
}
