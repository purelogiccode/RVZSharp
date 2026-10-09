using System.Diagnostics;
using System.Security.Cryptography;
using RVZSharp.Models;
using Xunit.Abstractions;

namespace RVZSharp.Slow.Tests;

/// <summary>
/// Optional differential validation against Dolphin's <c>dolphin-tool</c> and wit's
/// <c>wit</c>/<c>wwt</c>. The tools are looked up from <c>RVZ_DOLPHIN_TOOL</c>,
/// <c>RVZ_WIT</c> and <c>RVZ_WWT</c> (or the PATH), and the input disc image from
/// <c>RVZ_DIFF_ISO</c>; every test no-ops when the tool or the ISO is unavailable.
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
