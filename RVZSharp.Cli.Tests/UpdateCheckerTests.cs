namespace RVZSharp.Cli.Tests;

/// <summary>
/// Tests for <see cref="UpdateChecker"/>'s release-tag parsing (the versions compared
/// against the running build) and the <c>RVZSHARP_NO_UPDATE_CHECK</c> switch.
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>Verifies that accepted release tags parse to their semantic version.</summary>
    /// <param name="tag">The release tag.</param>
    /// <param name="major">The expected major version.</param>
    /// <param name="minor">The expected minor version.</param>
    /// <param name="build">The expected build number (-1 when absent).</param>
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0.1", 2, 0, 1)]
    [InlineData("v2.0", 2, 0, -1)]
    [InlineData("v1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+build.5", 1, 2, 3)]
    [InlineData("v1.2.3-rc1+build.7", 1, 2, 3)]
    [InlineData("v1.2.3.4", 1, 2, 3)]
    [InlineData("  v1.2.3  ", 1, 2, 3)]
    public void ParseTag_ValidTags_Parse(string tag, int major, int minor, int build)
    {
        var version = UpdateChecker.ParseTag(tag);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    /// <summary>Verifies that non-version tags parse to null.</summary>
    /// <param name="tag">The invalid tag.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("garbage")]
    [InlineData("vNext")]
    [InlineData("1.x")]
    [InlineData("release-2024")]
    public void ParseTag_InvalidTags_ReturnNull(string? tag)
    {
        Assert.Null(UpdateChecker.ParseTag(tag));
    }

    /// <summary>Verifies that parsed tags compare as semantic versions for update decisions.</summary>
    [Fact]
    public void ParseTag_SupportsVersionComparison()
    {
        var current = UpdateChecker.ParseTag("v1.1.0")!;

        Assert.True(UpdateChecker.ParseTag("v1.2.0")! > current);
        Assert.True(UpdateChecker.ParseTag("v2.0.0")! > current);
        Assert.True(UpdateChecker.ParseTag("v1.1.0.1")! > current);
        Assert.True(UpdateChecker.ParseTag("v1.0.0")! < current);
        Assert.Equal(current, UpdateChecker.ParseTag("1.1.0"));
    }

    /// <summary>Verifies that the check is disabled when <c>RVZSHARP_NO_UPDATE_CHECK</c> is set.</summary>
    [Fact]
    public void IsDisabled_ReflectsEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", null);
            Assert.False(UpdateChecker.IsDisabled);

            Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", "1");
            Assert.True(UpdateChecker.IsDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", original);
        }
    }

    /// <summary>Verifies that a disabled check returns null without any network access.</summary>
    [Fact]
    public async Task CheckAsync_WhenDisabled_ReturnsNull()
    {
        var original = Environment.GetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", "1");
            using var checker = new UpdateChecker();
            Assert.Null(await checker.CheckAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RVZSHARP_NO_UPDATE_CHECK", original);
        }
    }
}
