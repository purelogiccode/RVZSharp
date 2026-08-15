using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Tests;

/// <summary>
/// Byte-exact validation of the decoder and writer against REAL RVZ files on disk.
///
/// The expected ISO SHA-1 hashes come from the official No-Intro DAT files
/// (<c>References/rvz-1.0.3/testdata/Nintendo - GameCube - Datfile…dat</c> and
/// <c>Nintendo - Wii - Datfile…dat</c>). A test that decodes a real file to its No-Intro
/// SHA-1 proves the reader reproduces the original disc image byte-for-byte.
///
/// The files live on a local drive (<c>F:\Nintendo GameCube</c> / <c>F:\Nintendo Wii</c>)
/// and are NOT part of the repository, so every test early-returns (no-op) when its file
/// is absent. Run the suite on a machine that has the games mounted to execute them.
/// </summary>
public static class RealRvzCatalog
{
    /// <summary>(display name, relative file name, expected ISO SHA-1, expected ISO size).</summary>
    public record RealRvz(string Name, string File, string Sha1, long IsoSize);

    public const string GcDir = @"F:\Nintendo GameCube";
    public const string WiiDir = @"F:\Nintendo Wii";

    public static readonly RealRvz[] GameCube =
    [
        new("Advance Game Port (Unl)", "Advance Game Port (USA) (Unl).rvz",
            "305fe256e4927b1e8fb54a02e886197b97263508", 1459978240),
        new("Advance Game Port (Unl) (Rev 1)", "Advance Game Port (USA) (Unl) (Rev 1).rvz",
            "4e448ab4189f3ab09f4b5a9dbf2355792d8c956e", 1459978240),
        new("Call of Duty - Finest Hour", "Call of Duty - Finest Hour (USA).rvz",
            "4ce36ab8246ee4d11f636cd89b6906b07ceb5519", 1459978240),
        new("Crash Bandicoot - The Wrath of Cortex", "Crash Bandicoot - The Wrath of Cortex (USA).rvz",
            "08baf3fdef38908ee2d0a826afc728198048bd38", 1459978240),
        new("Evolution Snowboarding", "Evolution Snowboarding (USA).rvz",
            "3e6dc183cb2bb248e443f15b14eacb1d174016a0", 1459978240),
        new("Game Boy Player Start-Up Disc (Rev 2)", "Game Boy Player Start-Up Disc (USA) (Rev 2).rvz",
            "f2439bbe1ff64133050fbc00574be8478210a958", 1459978240),
        new("Harvest Moon - A Wonderful Life", "Harvest Moon - A Wonderful Life (USA).rvz",
            "8d0f26063d0ebf2ea3e7a18e86de01b8cc1e5191", 1459978240),
        new("Kelly Slater's Pro Surfer", "Kelly Slater's Pro Surfer (USA).rvz",
            "fe8b890354a796ceae9efd8316706ccc65e41861", 1459978240),
        new("Midway Arcade Treasures", "Midway Arcade Treasures (USA).rvz",
            "cd0c7f3fc49bbe42bda3eb6494a027c90adfb82c", 1459978240),
        new("Monster Jam - Maximum Destruction", "Monster Jam - Maximum Destruction (USA).rvz",
            "57fcc43b8c74c6e631c701e59e2a5f27d120cf60", 1459978240),
        new("Open Season (En,Fr,Es)", "Open Season (USA) (En,Fr,Es).rvz",
            "61e04f1dbfea8c34024638bbfda544b50abe33fe", 1459978240),
        new("RedCard 20-03", "RedCard 20-03 (USA).rvz",
            "44b78ec19415f9f9a5a7f294f482d8011e8716b0", 1459978240),
        new("Robots", "Robots (USA).rvz",
            "98cef132f3ae139414d089df2e09b9c63d7833e7", 1459978240),
        new("Sum of All Fears, The", "Sum of All Fears, The (USA).rvz",
            "c7214e84362f41983703328a187f4e056177da37", 1459978240),
        new("Tak and the Power of Juju", "Tak and the Power of Juju (USA).rvz",
            "ac9b16004e7a8eb87e5acebb5c095541ace72e18", 1459978240)
    ];

    public static readonly RealRvz[] Wii =
    [
        new("Big Brain Academy - Wii Degree", "Big Brain Academy - Wii Degree (USA) (En,Fr,Es).rvz",
            "37896d2a60172695467d911e1d77d02f846a9856", 4699979776),
        new("Cabela's Monster Buck Hunter", "Cabela's Monster Buck Hunter (USA).rvz",
            "5961af841561e95c9c48778a53abb59f2036fe1f", 4699979776),
        new("Deadly Creatures", "Deadly Creatures (USA) (En,Fr,Es).rvz",
            "c75f7b0f0ba13626ddab9412e2b6bb71ea4b584e", 4699979776),
        new("DreamWorks Kung Fu Panda", "DreamWorks Kung Fu Panda (USA) (En,Fr).rvz",
            "b92744a9eff56631bf654e5f4d003ecdb464581a", 4699979776),
        new("Guitar Hero - Aerosmith", "Guitar Hero - Aerosmith (USA) (En,Fr).rvz",
            "b95dab2697b4f0571f5512b3fcce1a5a75e021eb", 4699979776),
        new("Iron Man (Rev 1)", "Iron Man (USA) (En,Fr,Es) (Rev 1).rvz",
            "fbab7486ae979e7937a2a74337a9f77121e61e22", 4699979776),
        new("Just Dance 2014", "Just Dance 2014 (USA) (En,Fr,Es).rvz",
            "5200b41d771c3f5f71fdf45752e27d44d3cffb64", 4699979776),
        new("KidFit Island Resort", "KidFit Island Resort (USA).rvz",
            "f6b63c18a1fac23fe535e4e98d4f5b0e1dcfac6f", 4699979776),
        new("Mountain Sports", "Mountain Sports (USA) (En,Fr).rvz",
            "17f888209833e3b36f86ec963e9d85b506a7507e", 4699979776),
        new("NASCAR Kart Racing", "NASCAR Kart Racing (USA).rvz",
            "998b8829e421ec33785311bd7932667bcb5dd08e", 4699979776),
        new("Nickelodeon SpongeBob's Boating Bash", "Nickelodeon SpongeBob's Boating Bash (USA).rvz",
            "fbabb5b292f9f5918dcc21cc29e1b7d384b3633f", 4699979776),
        new("Resident Evil - The Umbrella Chronicles", "Resident Evil - The Umbrella Chronicles (USA).rvz",
            "ae52bca6e1a0bf90d8e91cb62fbfba7a9ba750f7", 4699979776),
        new("Rig Racer 2", "Rig Racer 2 (USA).rvz",
            "924bf0e0e9827c7436245fe0feb4c01d852165e0", 4699979776),
        new("Smurfs 2, The", "Smurfs 2, The (USA) (En,Fr,Es).rvz",
            "ac13785a09a4ad45d5b7a741061cdc1a501caff6", 4699979776),
        new("Wii Fit Plus", "Wii Fit Plus (USA) (En,Fr,Es).rvz",
            "5b9c83266681293f16dafba0cfe5ac5775df0330", 4699979776)
    ];
}

/// <summary>Shared helpers for tests that run only when a real RVZ file is present.</summary>
public static class RealRvzOnly
{
    /// <summary>Full path of the catalog entry in <paramref name="dir"/>, or null when absent.</summary>
    public static string? PathIfPresent(RealRvzCatalog.RealRvz entry, string dir)
    {
        var path = Path.Combine(dir, entry.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Streaming SHA-1 of the entire decoded disc image.</summary>
    public static string Sha1(RvzReader reader)
    {
        using var sha = SHA1.Create();
        var buffer = new byte[1 << 20];
        var pos = 0L;
        while (pos < reader.Length)
        {
            var read = reader.ReadAt(pos, buffer);
            Assert.True(read > 0, $"Decode stopped at offset 0x{pos:X} (image is {reader.Length} bytes).");
            sha.TransformBlock(buffer, 0, read, null, 0);
            pos += read;
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

/// <summary>
/// Category 1 — decode: each real RVZ file decodes byte-for-byte to its official No-Intro
/// SHA-1 and the expected ISO size. This is the strongest real-world proof the reader
/// reproduces actual game images exactly.
/// </summary>
public class RealRvzDecodeTests
{
    public static TheoryData<string, string, string> Files()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var e in RealRvzCatalog.GameCube) data.Add(RealRvzCatalog.GcDir, e.File, e.Sha1);
        foreach (var e in RealRvzCatalog.Wii) data.Add(RealRvzCatalog.WiiDir, e.File, e.Sha1);
        return data;
    }

    /// <summary>
    /// Guard against silently shrinking coverage: when the game drive is mounted, ALL
    /// catalog files must be present (a typo'd catalog entry would otherwise make every
    /// dependent test a green no-op).
    /// </summary>
    [Fact]
    public void CatalogIsFullyPresent_WhenDriveIsMounted()
    {
        Assert.Equal(15, RealRvzCatalog.GameCube.Length);
        Assert.Equal(15, RealRvzCatalog.Wii.Length);

        var missing = new List<string>();
        foreach (var e in RealRvzCatalog.GameCube)
        {
            var path = Path.Combine(RealRvzCatalog.GcDir, e.File);
            if (File.Exists(path) && Directory.Exists(RealRvzCatalog.GcDir))
            {
                continue;
            }

            if (Directory.Exists(RealRvzCatalog.GcDir)) missing.Add(path);
        }

        foreach (var e in RealRvzCatalog.Wii)
        {
            var path = Path.Combine(RealRvzCatalog.WiiDir, e.File);
            if (File.Exists(path) && Directory.Exists(RealRvzCatalog.WiiDir))
            {
                continue;
            }

            if (Directory.Exists(RealRvzCatalog.WiiDir)) missing.Add(path);
        }

        // The drive is absent on machines without the games — the per-file tests no-op
        // there, so this guard does too. When the drive IS mounted, nothing may be missing.
        if (Directory.Exists(RealRvzCatalog.GcDir) ||
            Directory.Exists(RealRvzCatalog.WiiDir))
        {
            Assert.Empty(missing);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesToExpectedNoIntroSha1(string dir, string file, string expectedSha1)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        Assert.Equal(expectedSha1, RealRvzOnly.Sha1(reader));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReportsExpectedIsoSize(string dir, string file, string _)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var reader = RvzReader.Open(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), leaveOpen: false);
        Assert.Equal(FindSize(dir, file), reader.Length);
    }

    private static long FindSize(string dir, string file)
    {
        switch (dir)
        {
            case RealRvzCatalog.GcDir:
                {
                    foreach (var e in RealRvzCatalog.GameCube)
                        if (e.File == file)
                            return e.IsoSize;

                    break;
                }
            case RealRvzCatalog.WiiDir:
                {
                    foreach (var e in RealRvzCatalog.Wii)
                        if (e.File == file)
                            return e.IsoSize;

                    break;
                }
        }

        return -1;
    }
}

/// <summary>
/// Category 2 — container structure: the parsed file head / disc struct on real files is
/// self-consistent (RVZ magic, current version, declared physical file size, a valid RVZ
/// compression method, a legal chunk size, and a populated group table).
/// </summary>
public class RealRvzStructureTests
{
    public static TheoryData<string, string> GcFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in RealRvzCatalog.GameCube) data.Add(RealRvzCatalog.GcDir, e.File);
        return data;
    }

    public static TheoryData<string, string> WiiFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in RealRvzCatalog.Wii) data.Add(RealRvzCatalog.WiiDir, e.File);
        return data;
    }

    [Theory]
    [MemberData(nameof(GcFiles))]
    [MemberData(nameof(WiiFiles))]
    public void HeaderAndDiscAreValid(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);

        Assert.True(reader.FileHead.IsRvz, "expected an RVZ file");
        Assert.Equal(WiaFileHead.ImplementedVersion, reader.FileHead.Version);
        Assert.Equal((ulong)fs.Length, reader.FileHead.RvzFileSize);

        Assert.NotEqual(CompressionType.Purge, reader.Disc.Compression);
        Assert.Contains(reader.Disc.Compression, new[]
        {
            CompressionType.None, CompressionType.Bzip2, CompressionType.Lzma,
            CompressionType.Lzma2, CompressionType.Zstd
        });

        var chunk = reader.Disc.ChunkSize;
        var pow2 = (chunk & (chunk - 1)) == 0;
        Assert.True(pow2 || chunk % WiaDisc.GroupSize == 0,
            $"chunk size {chunk} is not a legal RVZ chunk size");
        Assert.Equal((int)chunk, reader.BlockSize);

        Assert.True(reader.GroupEntries.Length > 0, "group table is empty");
    }
}

/// <summary>
/// Category 3 — random access: <c>ReadAt</c> across chunk boundaries matches the streaming
/// full read, and out-of-range reads are clamped. Runs on a couple of representative files.
/// </summary>
public class RealRvzRegionTests
{
    [Fact]
    public void ReadFully_GameCube_MatchesCatalogSha1()
    {
        var e = RealRvzCatalog.GameCube[^1]; // Tak and the Power of Juju
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var full = reader.ReadFully(); // GameCube image (~1.4 GiB) fits in a byte[].
        Assert.Equal(e.Sha1, Convert.ToHexString(SHA1.HashData(full)).ToLowerInvariant());
    }

    [Fact]
    public void ReadAtAroundChunkBoundaries_GameCube_MatchesFullRead()
    {
        var e = RealRvzCatalog.GameCube[12]; // Robots
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var full = reader.ReadFully();

        var chunk = reader.BlockSize;
        long[] offsets = [0, chunk - 17, chunk, chunk + 5, 2 * chunk - 3, 7 * chunk + 11];
        foreach (var off in offsets)
        {
            if (off >= full.Length) continue;

            var count = (int)Math.Min(4096, full.Length - off);
            var expected = full.AsSpan((int)off, count).ToArray();
            var actual = new byte[count];
            Assert.Equal(count, reader.ReadAt(off, actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ReadAt_OutOfRange_ReturnsZero_Wii()
    {
        var e = RealRvzCatalog.Wii[^1]; // Wii Fit Plus
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.WiiDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var buffer = new byte[16];
        Assert.Equal(0, reader.ReadAt(reader.Length, buffer));
        Assert.Equal(0, reader.ReadAt(reader.Length + 100, buffer));
    }
}

/// <summary>
/// Category 4 — writer round-trip: re-encode a REAL RVZ file back to RVZ with the default
/// options, then decode the output; its SHA-1 must equal the original No-Intro hash. This
/// exercises the writer + reader on real disc data (raw data, decrypted Wii partitions,
/// hash trees, packing) end-to-end.
/// </summary>
public class RealRvzWriteRoundTripTests
{
    [Fact]
    public void RealGameCube_ReencodedToRvz_DecodesBackToSameSha1()
    {
        var e = RealRvzCatalog.GameCube[^1]; // Tak and the Power of Juju
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        ReencodeAndVerifySha1(path, e.Sha1);
    }

    [Fact]
    public void RealWii_ReencodedToRvz_DecodesBackToSameSha1()
    {
        var e = RealRvzCatalog.Wii[9]; // NASCAR Kart Racing
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.WiiDir);
        if (path is null) return;

        ReencodeAndVerifySha1(path, e.Sha1);
    }

    private static void ReencodeAndVerifySha1(string path, string expectedSha1)
    {
        using var decoded = Blob.Open(path);
        var filename = Path.Combine(
            Path.GetTempPath(), "rvzsharp_roundtrip_" + Guid.NewGuid().ToString("N") + ".rvz");
        try
        {
            using (var outFile = File.Create(filename))
            {
                RvzWriter.Write(decoded, outFile, RvzWriteOptions.Default);
            }

            using var fs = File.OpenRead(filename);
            using var reader = RvzReader.Open(fs, leaveOpen: true);
            Assert.Equal(expectedSha1, RealRvzOnly.Sha1(reader));
        }
        finally
        {
            File.Delete(filename);
        }
    }
}