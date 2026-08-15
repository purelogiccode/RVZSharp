using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class NfsBlobTests
{
    private static byte[] MakeKey() => Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();

    [Fact]
    public void RoundTrip_WithMissingBlock()
    {
        var key = MakeKey();
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, 5, [(0, 2), (4, 1)]);

        using var reader = NfsBlob.Open(new MemoryStream(nfs), key);
        Assert.Equal(BlobType.Nfs, reader.Type);
        Assert.Equal(5L * 0x8000, reader.Length);

        var probe = new byte[5 * 0x8000];
        reader.ReadAt(0, probe);
        Assert.Equal(iso, probe);
    }

    [Fact]
    public void RoundTrip_WrongKey_GivesGarbage()
    {
        var key = MakeKey();
        var wrongKey = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);

        using var reader = NfsBlob.Open(new MemoryStream(nfs), wrongKey);
        var probe = new byte[2 * 0x8000];
        reader.ReadAt(0, probe);
        Assert.NotEqual(iso, probe);
    }

    [Fact]
    public void BlockZero_IsMarkedUnencrypted()
    {
        // The reader forces byte 0x61 of block 0 to 1; the builder mirrors that, so the
        // decoded output must have it set regardless of the stored ciphertext.
        var key = MakeKey();
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, 1, [(0, 1)]);

        using var reader = NfsBlob.Open(new MemoryStream(nfs), key);
        var probe = new byte[0x8000];
        reader.ReadAt(0, probe);
        Assert.Equal(1, probe[0x61]);
        Assert.Equal(iso, probe);
    }

    [Fact]
    public void KeyFromHtkBin_OnDisk()
    {
        var key = MakeKey();
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);

        var root = Path.Combine(Path.GetTempPath(), "rvzsharp-nfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var contentDir = Path.Combine(root, "content");
            Directory.CreateDirectory(contentDir);
            var codeDir = Path.Combine(root, "code");
            Directory.CreateDirectory(codeDir);
            var nfsPath = Path.Combine(contentDir, "hif_000000.nfs");
            File.WriteAllBytes(nfsPath, nfs);
            File.WriteAllBytes(Path.Combine(codeDir, "htk.bin"), key);

            using var stream = File.OpenRead(nfsPath);
            using var reader = NfsBlob.Open(stream, nfsPath, leaveOpen: true);
            var probe = new byte[2 * 0x8000];
            reader.ReadAt(0, probe);
            Assert.Equal(iso, probe);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingKey_Throws()
    {
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);

        Assert.Throws<RvzUnsupportedException>(() => NfsBlob.Open(new MemoryStream(nfs)));
    }

    [Fact]
    public void WrongDirectoryName_Throws()
    {
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);

        var root = Path.Combine(Path.GetTempPath(), "rvzsharp-nfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "notcontent");
            Directory.CreateDirectory(dir);
            var nfsPath = Path.Combine(dir, "hif_000000.nfs");
            File.WriteAllBytes(nfsPath, nfs);

            using var stream = File.OpenRead(nfsPath);
            Assert.Throws<RvzFormatException>(() => NfsBlob.Open(stream, nfsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BadMagic_ThrowsFormatException()
    {
        var bytes = new byte[0x200];
        Assert.Throws<RvzFormatException>(() => NfsBlob.Open(new MemoryStream(bytes), MakeKey()));
    }

    [Fact]
    public void SingleFileMode_TooSmall_Throws()
    {
        // Dolphin validates the raw size in every mode (NFSBlob.cpp:96-103): a stream that
        // cannot cover the declared LBA ranges must be rejected, not read as garbage.
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);
        var trimmed = nfs.AsSpan(0, nfs.Length - 0x100).ToArray();

        Assert.Throws<RvzFormatException>(() => NfsBlob.Open(new MemoryStream(trimmed), key));
    }

    [Fact]
    public void WrongFileName_Throws()
    {
        // Dolphin requires the file to be named hif_000000.nfs (NFSBlob.cpp:129-132).
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 2, [(0, 2)]);
        var root = Path.Combine(Path.GetTempPath(), "rvzsharp-test-nfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "content"));
        Directory.CreateDirectory(Path.Combine(root, "code"));
        var path = Path.Combine(root, "content", "wrong.nfs");
        File.WriteAllBytes(path, nfs);
        File.WriteAllBytes(Path.Combine(root, "code", "htk.bin"), key);
        try
        {
            using var stream = File.OpenRead(path);
            Assert.Throws<RvzFormatException>(() => NfsBlob.Open(stream, path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
