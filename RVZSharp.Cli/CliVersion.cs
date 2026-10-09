using System.Reflection;

namespace RVZSharp.Cli;

/// <summary>
/// The CLI's product version: the assembly's informational version with build metadata
/// stripped (e.g. <c>1.1.0</c>), so telemetry, bug reports and the update check all agree.
/// </summary>
internal static class CliVersion
{
    /// <summary>The product version string (build metadata removed).</summary>
    public static string Product { get; } = ReadProductVersion();

    /// <summary>The parsed product version, or <c>0.0</c> when it cannot be parsed.</summary>
    public static Version Parsed { get; } = Parse(Product) ?? new Version(0, 0);

    private static string ReadProductVersion()
    {
        var assembly = typeof(CliVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var cut = informational.IndexOf('+');
        return cut >= 0 ? informational[..cut] : informational;
    }

    private static Version? Parse(string text)
    {
        var cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }
}
