using Xunit.Abstractions;

namespace RVZSharp.Slow.Tests;

/// <summary>
/// Optional real-world validation driven by environment variables
/// (RVZ_REAL_FILE / RVZ_REAL_SHA1) — kept in the slow suite on purpose.
/// </summary>
public class RealFileDecodeTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public RealFileDecodeTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    /// <summary>
    /// Optional real-world validation: set RVZ_REAL_FILE to a real .rvz path (and optionally
    /// RVZ_REAL_SHA1 to the expected SHA-1 of the decoded ISO) to run it.
    /// </summary>
    [Fact]
    public void DecodeRealFile()
    {
        var path = Environment.GetEnvironmentVariable("RVZ_REAL_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return; // skipped unless explicitly requested
        }

        using var file = File.OpenRead(path);
        using var reader = RvzReader.Open(file, leaveOpen: true);
        using var sha1 = System.Security.Cryptography.SHA1.Create();

        var buffer = new byte[1 << 20];
        var position = 0L;
        while (position < reader.Length)
        {
            var read = reader.ReadAt(position, buffer);
            Assert.True(read > 0, $"Read stopped at 0x{position:X}");
            sha1.TransformBlock(buffer, 0, read, null, 0);
            position += read;
        }

        sha1.TransformFinalBlock([], 0, 0);
        var actual = Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
        var expected = Environment.GetEnvironmentVariable("RVZ_REAL_SHA1");
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(expected.Trim().ToLowerInvariant(), actual);
        }
        else
        {
            _testOutputHelper.WriteLine($"decoded {path}: {reader.Length} bytes, sha1={actual}");
        }
    }
}