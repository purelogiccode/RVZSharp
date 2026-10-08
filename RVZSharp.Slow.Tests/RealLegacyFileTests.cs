using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using Xunit.Abstractions;

namespace RVZSharp.Slow.Tests;

/// <summary>
/// Optional real-world validation of the legacy container readers, driven by environment
/// variables — kept in the slow suite on purpose. Set <c>RVZ_REAL_GCZ</c>, <c>RVZ_REAL_CISO</c>,
/// <c>RVZ_REAL_WBFS</c>, <c>RVZ_REAL_TGC</c>, <c>RVZ_REAL_WIA</c> (and for NFS both
/// <c>RVZ_REAL_NFS</c> and <c>RVZ_REAL_NFS_KEY</c>, 32 hex characters) to a real file, and
/// optionally <c>&lt;VAR&gt;_SHA1</c> to the expected SHA-1 of the decoded image. Every test
/// no-ops when its variable is unset, so the suite stays green without the files.
/// </summary>
public class RealLegacyFileTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public RealLegacyFileTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void DecodeRealGcz() => Decode("RVZ_REAL_GCZ");

    [Fact]
    public void DecodeRealCiso() => Decode("RVZ_REAL_CISO");

    [Fact]
    public void DecodeRealWbfs() => Decode("RVZ_REAL_WBFS");

    [Fact]
    public void DecodeRealTgc() => Decode("RVZ_REAL_TGC");

    [Fact]
    public void DecodeRealWia() => Decode("RVZ_REAL_WIA");

    [Fact]
    public void DecodeRealNfs()
    {
        var path = Environment.GetEnvironmentVariable("RVZ_REAL_NFS");
        if (string.IsNullOrEmpty(path))
        {
            return; // skipped unless explicitly requested
        }

        var keyHex = Environment.GetEnvironmentVariable("RVZ_REAL_NFS_KEY");
        if (string.IsNullOrEmpty(keyHex))
        {
            _testOutputHelper.WriteLine("RVZ_REAL_NFS is set but RVZ_REAL_NFS_KEY is missing; skipping.");
            return;
        }

        using var blob = Blob.Open(path, Convert.FromHexString(keyHex));
        AssertDecodes(blob, path, "RVZ_REAL_NFS_SHA1");
    }

    private void Decode(string envVar)
    {
        var path = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(path))
        {
            return; // skipped unless explicitly requested
        }

        using var blob = Blob.Open(path);
        AssertDecodes(blob, path, envVar + "_SHA1");
    }

    private void AssertDecodes(IBlobReader blob, string path, string sha1EnvVar)
    {
        var hashes = DiscHasher.Compute(blob);
        var actual = Convert.ToHexString(hashes.Sha1).ToLowerInvariant();
        var expected = Environment.GetEnvironmentVariable(sha1EnvVar);
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(expected.Trim().ToLowerInvariant(), actual);
        }
        else
        {
            _testOutputHelper.WriteLine(
                $"decoded {path}: {blob.Length} bytes ({blob.Type}), sha1={actual}");
        }
    }
}
