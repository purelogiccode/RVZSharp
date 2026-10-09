namespace RVZSharp.Cli.Tests;

/// <summary>
/// Tests for the CLI's option parser (<see cref="Program.ParseArgs"/>) and the shared product
/// version helper.
/// </summary>
public class ProgramParsingTests
{
    private static readonly Dictionary<string, Program.OptionSpec> Spec = new()
    {
        ["input"] = new Program.OptionSpec("-i", true, null),
        ["format"] = new Program.OptionSpec("-f", true, ["iso", "rvz"]),
        ["scrub"] = new Program.OptionSpec("-s", false, null),
        ["json"] = new Program.OptionSpec("--json", false, null)
    };

    /// <summary>Verifies that long options, values and flags are parsed.</summary>
    [Fact]
    public void ParseArgs_OptionsValuesAndFlags()
    {
        var result = Program.ParseArgs(
            ["--input", "game.iso", "-f", "rvz", "--scrub", "--json", "positional"], Spec);

        Assert.Equal("game.iso", result.Get("input"));
        Assert.Equal("rvz", result.Get("format"));
        Assert.True(result.IsSet("scrub"));
        Assert.True(result.HasFlag("json"));
        Assert.Equal(["positional"], result.Positionals);
    }

    /// <summary>Verifies that an inline value is accepted for value options.</summary>
    [Fact]
    public void ParseArgs_InlineValue()
    {
        var result = Program.ParseArgs(["--input=game.rvz"], Spec);
        Assert.Equal("game.rvz", result.Get("input"));
    }

    /// <summary>Verifies that an inline value on a flag is rejected instead of silently ignored.</summary>
    [Fact]
    public void ParseArgs_FlagWithInlineValue_Throws()
    {
        var exception = Assert.Throws<Program.CliErrorException>(() => Program.ParseArgs(["--scrub=no"], Spec));
        Assert.Contains("does not take an argument", exception.Message);
    }

    /// <summary>Verifies that an unknown option is rejected.</summary>
    [Fact]
    public void ParseArgs_UnknownOption_Throws()
    {
        Assert.Throws<Program.CliErrorException>(() => Program.ParseArgs(["--nope"], Spec));
        Assert.Throws<Program.CliErrorException>(() => Program.ParseArgs(["-q"], Spec));
    }

    /// <summary>Verifies that a value option without a value is rejected.</summary>
    [Fact]
    public void ParseArgs_MissingValue_Throws()
    {
        var exception = Assert.Throws<Program.CliErrorException>(() => Program.ParseArgs(["--input"], Spec));
        Assert.Contains("requires an argument", exception.Message);
    }

    /// <summary>Verifies that an invalid choice is rejected.</summary>
    [Fact]
    public void ParseArgs_InvalidChoice_Throws()
    {
        var exception = Assert.Throws<Program.CliErrorException>(() => Program.ParseArgs(["-f", "gcz"], Spec));
        Assert.Contains("invalid choice", exception.Message);
    }

    /// <summary>Verifies that the product version matches the released version and parses.</summary>
    [Fact]
    public void CliVersion_MatchesProductVersion()
    {
        Assert.StartsWith("1.1.0", CliVersion.Product);
        Assert.Equal(new Version(1, 1, 0), CliVersion.Parsed);
    }
}
