namespace RVZSharp.Cli.Tests;

/// <summary>
/// Tests for the extract path guards (<see cref="Program.IsSafePathSegment"/> and
/// <see cref="Program.BuildSingleTarget"/>): user-supplied -s paths and image-controlled
/// file names must never escape the output folder.
/// </summary>
public class ExtractPathTests
{
    /// <summary>Verifies that ordinary file and folder names are accepted.</summary>
    [Theory]
    [InlineData("sys")]
    [InlineData("boot.bin")]
    [InlineData("update 1")]
    [InlineData("file-name_v2.3")]
    public void IsSafePathSegment_OrdinaryNames_AreSafe(string segment)
    {
        Assert.True(Program.IsSafePathSegment(segment));
    }

    /// <summary>Verifies that traversal and absolute-path segments are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/abs")]
    public void IsSafePathSegment_TraversalSegments_AreUnsafe(string segment)
    {
        Assert.False(Program.IsSafePathSegment(segment));
    }

    /// <summary>Verifies that Dolphin-style absolute -s paths stay under the output folder.</summary>
    [Theory]
    [InlineData("sys/boot.bin")]
    [InlineData("/sys/boot.bin")]
    [InlineData("files///hello.txt")]
    public void BuildSingleTarget_DolphinStylePaths_StayUnderBase(string singlePath)
    {
        var target = Program.BuildSingleTarget("out", singlePath);
        var expected = singlePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(Path.Combine("out", "files"), Path.Combine);
        Assert.Equal(expected, target);
    }

    /// <summary>Verifies that escaping -s paths are rejected.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../evil.bin")]
    [InlineData("sys/../../evil.bin")]
    [InlineData("sys\\boot.bin")]
    public void BuildSingleTarget_EscapingPaths_Throw(string singlePath)
    {
        Assert.Throws<Program.CliErrorException>(() => Program.BuildSingleTarget("out", singlePath));
    }
}
