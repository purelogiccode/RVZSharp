using RVZSharp.Cli;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Tests for <see cref="Completions"/>: every supported shell name returns a script (case
/// insensitively), unsupported names return null, and each script covers the command surface.
/// </summary>
public class CompletionsTests
{
    /// <summary>Verifies that every supported shell name returns a completion script.</summary>
    /// <param name="shell">The shell name passed to the completions command.</param>
    [Theory]
    [InlineData("bash")]
    [InlineData("BASH")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    [InlineData("PowerShell")]
    [InlineData("pwsh")]
    public void Get_SupportedShell_ReturnsScript(string shell)
    {
        Assert.NotNull(Completions.Get(shell));
    }

    /// <summary>Verifies that an unsupported shell name returns null.</summary>
    /// <param name="shell">The unsupported shell name.</param>
    [Theory]
    [InlineData("nope")]
    [InlineData("cmd")]
    [InlineData("")]
    public void Get_UnsupportedShell_ReturnsNull(string shell)
    {
        Assert.Null(Completions.Get(shell));
    }

    /// <summary>Verifies that every script mentions the command surface and the option names.</summary>
    [Fact]
    public void Get_Scripts_ContainCommandSurface()
    {
        foreach (var shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            var script = Completions.Get(shell);
            Assert.NotNull(script);
            Assert.Contains("rvzsharp", script);
            Assert.Contains("convert", script);
            Assert.Contains("verify", script);
            Assert.Contains("extract", script);
        }
    }

    /// <summary>Verifies that every script completes the option values (formats, codecs, algorithms).</summary>
    [Fact]
    public void Get_Scripts_ContainOptionValues()
    {
        foreach (var shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            var script = Completions.Get(shell);
            Assert.NotNull(script);
            Assert.Contains("zstd", script);
            Assert.Contains("lzma2", script);
            Assert.Contains("sha1", script);
            Assert.Contains("ciso", script);
        }
    }

    /// <summary>Verifies the shell-specific markers of each script.</summary>
    [Fact]
    public void Get_Scripts_UseShellSpecificSyntax()
    {
        Assert.StartsWith("# rvzsharp bash completion", Completions.Get("bash"));
        Assert.StartsWith("#compdef rvzsharp", Completions.Get("zsh"));
        Assert.Contains("complete -c rvzsharp", Completions.Get("fish"));
        Assert.Contains("Register-ArgumentCompleter", Completions.Get("powershell"));
    }
}
