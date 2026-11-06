using System.Runtime.InteropServices;
using RVZSharp.Cli.Logging;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Verifies the bug-report payload always contains every environment, error and exception
/// field required by the bug-report API.
/// </summary>
public class BugReportDetailsTests
{
    /// <summary>Verifies the complete report layout for an exception.</summary>
    [Fact]
    public void Build_WithException_ContainsAllRequiredFields()
    {
        var timestamp = new DateTimeOffset(2026, 11, 5, 12, 34, 56, TimeSpan.FromHours(-3));
        var exception = new InvalidOperationException("boom", new IOException("inner"));

        var report = BugReportDetails.Build(
            "Conversion failed", exception, "RVZSharp.Cli", "1.2.0", timestamp);

        Assert.Contains("=== Environment Details ===", report);
        Assert.Contains("Date: 2026-11-05 12:34:56 -03:00", report);
        Assert.Contains("Application Name: RVZSharp.Cli", report);
        Assert.Contains("Application Version: 1.2.0", report);
        Assert.Contains($"OS Version: {Environment.OSVersion}", report);
        Assert.Contains($"Architecture: {RuntimeInformation.OSArchitecture}", report);
        Assert.Contains($"Bitness: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}", report);
        Assert.Contains($"{BugReportDetails.PlatformLabel()} Version: {RuntimeInformation.OSDescription}", report);
        Assert.Contains($"Processor Count: {Environment.ProcessorCount}", report);
        Assert.Contains($"Base Directory: {AppContext.BaseDirectory}", report);
        Assert.Contains($"Temp Path: {Path.GetTempPath()}", report);
        Assert.Contains("=== Error Details ===", report);
        Assert.Contains("Conversion failed", report);
        Assert.Contains("=== Exception Details ===", report);
        Assert.Contains("Type: System.InvalidOperationException", report);
        Assert.Contains("Message: boom", report);
        Assert.Contains("Source:", report);
        Assert.Contains("StackTrace:", report);
    }

    /// <summary>Verifies a message-only report still has the exception section with placeholders.</summary>
    [Fact]
    public void Build_WithoutException_UsesPlaceholders()
    {
        var report = BugReportDetails.Build("Only a message", null, "RVZSharp.Cli", "1.2.0");

        Assert.Contains("=== Error Details ===", report);
        Assert.Contains("Only a message", report);
        Assert.Contains("=== Exception Details ===", report);
        Assert.Contains("Type: (none)", report);
        Assert.Contains("Message: (none)", report);
        Assert.Contains("Source: (none)", report);
        Assert.Contains("StackTrace: (none)", report);
    }

    /// <summary>Verifies a huge exception still keeps every required section and field.</summary>
    [Fact]
    public void Build_LongException_KeepsAllSectionsAndFields()
    {
        var exception = new InvalidOperationException(new string('x', 10_000));
        var report = BugReportDetails.Build(
            new string('y', 10_000), exception, "RVZSharp.Cli", "1.2.0");

        Assert.Contains("=== Environment Details ===", report);
        Assert.Contains("=== Error Details ===", report);
        Assert.Contains("=== Exception Details ===", report);
        Assert.Contains("Type: System.InvalidOperationException", report);
        Assert.Contains("Message:", report);
        Assert.Contains("Source:", report);
        Assert.Contains("StackTrace:", report);
        Assert.True(report.Length <= 4000, $"report was {report.Length} characters");
    }

    /// <summary>Verifies the platform label is one of the documented values.</summary>
    [Fact]
    public void PlatformLabel_ReturnsSupportedFamily()
    {
        Assert.Contains(BugReportDetails.PlatformLabel(), new[] { "Windows", "Linux", "MacOSX", "OS" });
    }
}
