using System.Security.Cryptography;
using System.Text;
using RVZSharp;
using RVZSharp.Format;

namespace RVZSharp.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "info")
            {
                return Info(args[1]);
            }

            if (args.Length >= 3 && args[0] == "decode")
            {
                return Decode(args[1], args[2], args);
            }

            Console.Error.WriteLine("""
                RVZSharp — Dolphin RVZ disc image tool

                Usage:
                  rvzsharp info <file.rvz>
                  rvzsharp decode <file.rvz> <out.iso> [--sha1 <expected-hex>]
                """);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Info(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = RvzReader.Open(file, leaveOpen: true);

        Console.WriteLine($"file:            {path}");
        Console.WriteLine($"format:          RVZ (version {WiaFileHead.FormatVersion(reader.FileHead.Version)})");
        Console.WriteLine($"disc type:       {reader.Disc.DiscType} ({(uint)reader.Disc.DiscType})");
        Console.WriteLine($"iso size:        {reader.Length} bytes (0x{reader.Length:X})");
        Console.WriteLine($"compression:     {reader.Disc.Compression} (level {reader.Disc.ComprLevel})");
        Console.WriteLine($"chunk size:      0x{reader.Disc.ChunkSize:X}");
        Console.WriteLine($"partitions:      {reader.Partitions.Length}");
        Console.WriteLine($"raw data areas:  {reader.RawDataEntries.Length}");
        Console.WriteLine($"groups:          {reader.GroupEntries.Length}");

        foreach (var part in reader.Partitions)
        {
            for (var s = 0; s < 2; s++)
            {
                var pd = part.Data[s];
                if (pd.NumSectors == 0)
                {
                    continue;
                }

                Console.WriteLine($"  partition @ sector {pd.FirstSector}: {pd.NumSectors} sectors, "
                    + $"{pd.NumGroups} groups (key {Convert.ToHexString(part.Key)})");
            }
        }

        foreach (var raw in reader.RawDataEntries)
        {
            Console.WriteLine($"  raw data @ 0x{raw.RawDataOffset:X}: 0x{raw.RawDataSize:X} bytes, "
                + $"{raw.NumGroups} groups");
        }

        return 0;
    }

    private static int Decode(string inputPath, string outputPath, string[] args)
    {
        string? expectedSha1 = null;
        for (var i = 3; i < args.Length - 1; i++)
        {
            if (args[i] == "--sha1")
            {
                expectedSha1 = args[i + 1];
            }
        }

        using var input = File.OpenRead(inputPath);
        using var reader = RvzReader.Open(input, leaveOpen: true);
        using var output = File.Create(outputPath);
        using var sha1 = SHA1.Create();

        var buffer = new byte[1 << 20];
        var position = 0L;
        var lastProgress = -1;
        while (position < reader.Length)
        {
            var read = reader.ReadAt(position, buffer);
            if (read <= 0)
            {
                throw new RvzFormatException($"Decoding stopped at offset 0x{position:X}.");
            }

            output.Write(buffer, 0, read);
            sha1.TransformBlock(buffer, 0, read, null, 0);
            position += read;

            if (Console.IsOutputRedirected)
            {
                continue;
            }

            var percent = (int)(position * 100 / reader.Length);
            if (percent != lastProgress)
            {
                lastProgress = percent;
                Console.Write($"\rdecoding... {percent,3}%");
            }
        }

        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine();
        }

        if (expectedSha1 != null)
        {
            sha1.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
            Console.WriteLine($"sha1: {actual}");
            if (!string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: SHA-1 mismatch (expected {expectedSha1}).");
                return 1;
            }
        }

        Console.WriteLine($"decoded {reader.Length} bytes to {outputPath}");
        return 0;
    }
}
