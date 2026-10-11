using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RVZSharp.Models;
using Xunit.Abstractions;

namespace RVZSharp.Slow.Tests;

/// <summary>
/// Optional differential validation against Dolphin's <c>dolphin-tool</c> and wit's
/// <c>wit</c>/<c>wwt</c>. The tools are looked up from <c>RVZ_DOLPHIN_TOOL</c>,
/// <c>RVZ_WIT</c> and <c>RVZ_WWT</c>, then <c>dolphin-tool</c> falls back to the
/// <c>DolphinTool.exe</c> shipped at the repository root, then to the PATH; the input
/// disc image comes from <c>RVZ_DIFF_ISO</c>. Every test no-ops when the tool or the
/// ISO is unavailable.
/// </summary>
public class DifferentialToolTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    /// <summary>Creates the test class with xUnit's output helper.</summary>
    /// <param name="testOutputHelper">The xUnit output helper for diagnostic messages.</param>
    public DifferentialToolTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    /// <summary>Verifies that dolphin-tool decodes an RVZ written by <c>RvzWriter</c> to the input ISO.</summary>
    [Fact]
    public void DolphinTool_DecodesOurRvzOutput()
    {
        var tool = FindTool("RVZ_DOLPHIN_TOOL", "dolphin-tool");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        WithTempDirectory(directory =>
        {
            var rvz = Path.Combine(directory, "ours.rvz");
            var outputIso = Path.Combine(directory, "out.iso");
            RvzWriter.Write(isoPath, rvz);
            RunTool(tool, "convert", "-i", rvz, "-o", outputIso, "-f", "iso");
            Assert.Equal(Sha1File(isoPath), Sha1File(outputIso));
        });
    }

    /// <summary>Verifies that <c>RvzReader</c> decodes an RVZ written by dolphin-tool to the input ISO.</summary>
    [Fact]
    public void OurReader_DecodesDolphinToolRvzOutput()
    {
        var tool = FindTool("RVZ_DOLPHIN_TOOL", "dolphin-tool");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        WithTempDirectory(directory =>
        {
            var rvz = Path.Combine(directory, "theirs.rvz");
            RunTool(tool, "convert", "-i", isoPath, "-o", rvz, "-f", "rvz",
                "-b", "2097152", "-c", "zstd", "-l", "5");
            using var reader = RvzReader.Open(rvz);
            var hashes = DiscHasher.Compute(reader);
            Assert.Equal(Sha1File(isoPath), Convert.ToHexString(hashes.Sha1).ToLowerInvariant());
        });
    }

    /// <summary>
    /// RVZ codec matrix (dolphin-tool <c>-c</c> name, level, block size): every supported
    /// RVZ compression method plus Dolphin's suggested 128 KiB block size. The names match
    /// <see cref="CompressionType"/> case-insensitively, so the same row drives both tools.
    /// </summary>
    public static TheoryData<string, int, int> RvzCodecMatrix() => new()
    {
        { "zstd", 5, 2097152 }, // library + Dolphin defaults
        { "zstd", 1, 2097152 }, // fast level
        { "zstd", 5, 131072 }, // Dolphin's suggested RVZ block size (small chunks)
        { "lzma2", 5, 2097152 },
        { "lzma", 6, 2097152 },
        { "bzip2", 5, 2097152 },
        { "none", 0, 2097152 }, // stored as-is; the level is ignored by both tools
    };

    /// <summary>
    /// Verifies that dolphin-tool decodes an RVZ written by <c>RvzWriter</c> for every
    /// matrix codec/level back to the input ISO.
    /// </summary>
    /// <param name="compression">The dolphin-tool <c>-c</c> name (also parsed as a <see cref="CompressionType"/>).</param>
    /// <param name="level">The compression level passed to both tools.</param>
    /// <param name="blockSize">The chunk/block size passed to both tools.</param>
    [Theory]
    [MemberData(nameof(RvzCodecMatrix))]
    public void DolphinTool_DecodesOurRvzOutput_CodecMatrix(string compression, int level, int blockSize)
    {
        var tool = FindTool("RVZ_DOLPHIN_TOOL", "dolphin-tool");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        _testOutputHelper.WriteLine($"encode with RVZSharp ({compression} level {level}, block {blockSize}), decode with {tool}");
        WithTempDirectory(directory =>
        {
            var rvz = Path.Combine(directory, "ours.rvz");
            var outputIso = Path.Combine(directory, "out.iso");
            RvzWriter.Write(isoPath, rvz, new RvzWriteOptions
            {
                Compression = Enum.Parse<CompressionType>(compression, ignoreCase: true),
                CompressionLevel = level,
                ChunkSize = blockSize
            });
            RunTool(tool, "convert", "-i", rvz, "-o", outputIso, "-f", "iso");
            Assert.Equal(Sha1File(isoPath), Sha1File(outputIso));
        });
    }

    /// <summary>
    /// Verifies that <c>RvzReader</c> decodes an RVZ written by dolphin-tool for every
    /// matrix codec/level back to the input ISO.
    /// </summary>
    /// <param name="compression">The dolphin-tool <c>-c</c> name.</param>
    /// <param name="level">The compression level passed to both tools.</param>
    /// <param name="blockSize">The chunk/block size passed to both tools.</param>
    [Theory]
    [MemberData(nameof(RvzCodecMatrix))]
    public void OurReader_DecodesDolphinToolRvzOutput_CodecMatrix(string compression, int level, int blockSize)
    {
        var tool = FindTool("RVZ_DOLPHIN_TOOL", "dolphin-tool");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        _testOutputHelper.WriteLine($"encode with {tool} ({compression} level {level}, block {blockSize}), decode with RVZSharp");
        WithTempDirectory(directory =>
        {
            var rvz = Path.Combine(directory, "theirs.rvz");
            RunTool(tool, "convert", "-i", isoPath, "-o", rvz, "-f", "rvz",
                "-b", blockSize.ToString(CultureInfo.InvariantCulture),
                "-c", compression,
                "-l", level.ToString(CultureInfo.InvariantCulture));
            using var reader = RvzReader.Open(rvz);
            var hashes = DiscHasher.Compute(reader);
            Assert.Equal(Sha1File(isoPath), Convert.ToHexString(hashes.Sha1).ToLowerInvariant());
        });
    }

    /// <summary>Verifies that wit decodes a CISO written by <c>CisoWriter</c> (the input prefix matches).</summary>
    [Fact]
    public void Wit_DecodesOurCisoOutput()
    {
        var tool = FindTool("RVZ_WIT", "wit");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        WithTempDirectory(directory =>
        {
            var ciso = Path.Combine(directory, "ours.ciso");
            var outputIso = Path.Combine(directory, "out.iso");
            CisoWriter.Write(isoPath, ciso);
            RunTool(tool, "copy", ciso, outputIso);

            // CISO decodes to the map capacity, so wit's ISO is at least the input size with a
            // zero tail; the input prefix must match byte-for-byte.
            var inputLength = new FileInfo(isoPath).Length;
            Assert.True(new FileInfo(outputIso).Length >= inputLength);
            Assert.Equal(Sha1Prefix(isoPath, inputLength), Sha1Prefix(outputIso, inputLength));
        });
    }

    /// <summary>Verifies that wwt decodes a TGC written by <c>TgcWriter</c> to the input ISO.</summary>
    [Fact]
    public void Wwt_DecodesOurTgcOutput()
    {
        var tool = FindTool("RVZ_WWT", "wwt");
        var isoPath = Environment.GetEnvironmentVariable("RVZ_DIFF_ISO");
        if (tool == null || string.IsNullOrEmpty(isoPath))
        {
            return;
        }

        WithTempDirectory(directory =>
        {
            var tgc = Path.Combine(directory, "ours.tgc");
            var outputIso = Path.Combine(directory, "out.iso");
            TgcWriter.Write(isoPath, tgc);
            RunTool(tool, "copy", tgc, outputIso);
            Assert.Equal(Sha1File(isoPath), Sha1File(outputIso));
        });
    }

    private static string? FindTool(string envVar, string name)
    {
        var explicitPath = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        // The repository ships DolphinTool.exe at its root, so the dolphin-tool
        // differential tests run on a fresh checkout with no setup.
        var repoRootTool = FindRepoRootTool(name);
        if (repoRootTool != null)
        {
            return repoRootTool;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows()
            ? new[] { name + ".exe", name + ".cmd", name + ".bat" }
            : [name];
        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            foreach (var candidate in names)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the reference tool shipped with the repository: <c>DolphinTool.exe</c> lives at
    /// the repository root, a few levels above the test output directory. Only
    /// <c>dolphin-tool</c> ships with the repo; <c>wit</c>/<c>wwt</c> still resolve via
    /// their environment variable or the PATH.
    /// </summary>
    /// <param name="name">The tool name passed to <see cref="FindTool"/>.</param>
    /// <returns>The repository-root tool path, or null when absent (e.g. non-Windows).</returns>
    private static string? FindRepoRootTool(string name)
    {
        if (!string.Equals(name, "dolphin-tool", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string fileName = "DolphinTool.exe";
        string? directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static void RunTool(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"{tool} {string.Join(' ', arguments)} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }

    private void WithTempDirectory(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("rvzsharp-diff-").FullName;
        _testOutputHelper.WriteLine($"working directory: {directory}");
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Sha1File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha1Prefix(string path, long length)
    {
        using var stream = File.OpenRead(path);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        var remaining = length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Assert.True(read > 0, $"prefix of {path} is shorter than {length} bytes");
            sha1.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }
}
