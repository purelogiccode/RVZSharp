using System.Security.Cryptography;
using RVZSharp;
using RVZSharp.Blobs;
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

            if (args.Length >= 3 && args[0] == "convert")
            {
                return ConvertToRvz(args[1], args[2], args);
            }

            Console.Error.WriteLine("""
                RVZSharp — Dolphin disc image tool

                Usage:
                  rvzsharp info <file.rvz|wia|gcz|ciso|wbfs|tgc|nfs|iso>
                  rvzsharp decode <file> <out.iso> [--sha1 <expected-hex>]
                  rvzsharp convert <file> <out.rvz> [--compression <none|zstd|bzip2|lzma|lzma2>]
                                        [--level <1-9>] [--chunk-size <kib>] [--no-packing]
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
        using var reader = Blob.Open(file, filePath: path, leaveOpen: true);

        Console.WriteLine($"file:            {path}");
        Console.WriteLine($"format:          {Blob.GetName(reader.Type)}");
        Console.WriteLine($"iso size:        {reader.Length} bytes (0x{reader.Length:X})");

        if (reader is RvzReader rvz)
        {
            Console.WriteLine($"version:         {WiaFileHead.FormatVersion(rvz.FileHead.Version)}");
            Console.WriteLine($"disc type:       {rvz.Disc.DiscType} ({(uint)rvz.Disc.DiscType})");
            Console.WriteLine($"compression:     {rvz.Disc.Compression} (level {rvz.Disc.ComprLevel})");
            Console.WriteLine($"chunk size:      0x{rvz.Disc.ChunkSize:X}");
            Console.WriteLine($"partitions:      {rvz.Partitions.Length}");
            Console.WriteLine($"raw data areas:  {rvz.RawDataEntries.Length}");
            Console.WriteLine($"groups:          {rvz.GroupEntries.Length}");

            foreach (var part in rvz.Partitions)
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

            foreach (var raw in rvz.RawDataEntries)
            {
                Console.WriteLine($"  raw data @ 0x{raw.RawDataOffset:X}: 0x{raw.RawDataSize:X} bytes, "
                    + $"{raw.NumGroups} groups");
            }
        }
        else if (reader is GczBlob gcz)
        {
            Console.WriteLine($"block size:      0x{gcz.BlockSize:X}");
            Console.WriteLine($"blocks:          {gcz.NumBlocks}");
            Console.WriteLine($"compression:     Deflate");
        }
        else if (reader.BlockSize != 0)
        {
            Console.WriteLine($"block size:      0x{reader.BlockSize:X}");
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
        using var reader = Blob.Open(input, filePath: inputPath, leaveOpen: true);
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

    private static int ConvertToRvz(string inputPath, string outputPath, string[] args)
    {
        var options = new RvzWriteOptions();
        for (var i = 3; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--compression":
                    options = options with { Compression = ParseCompression(args[i + 1]) };
                    break;
                case "--level":
                    options = options with { CompressionLevel = int.Parse(args[i + 1]) };
                    break;
                case "--chunk-size":
                    options = options with { ChunkSize = int.Parse(args[i + 1]) * 1024 };
                    break;
                case "--no-packing":
                    options = options with { Packing = false };
                    break;
            }
        }

        using var input = File.OpenRead(inputPath);
        using var blob = Blob.Open(input, filePath: inputPath, leaveOpen: true);
        using var output = File.Create(outputPath);
        RvzWriter.Write(blob, output, options);

        Console.WriteLine($"converted {blob.Length} bytes ({Blob.GetName(blob.Type)}) to {outputPath} "
            + $"({options.Compression}, level {options.CompressionLevel}, "
            + $"chunk 0x{options.ChunkSize:X}, packing {(options.Packing ? "on" : "off")})");
        return 0;
    }

    private static CompressionType ParseCompression(string name) =>
        name.ToLowerInvariant() switch
        {
            "none" => CompressionType.None,
            "purge" => CompressionType.Purge,
            "bzip2" or "bzip" => CompressionType.Bzip2,
            "lzma" => CompressionType.Lzma,
            "lzma2" => CompressionType.Lzma2,
            "zstd" or "zstandard" => CompressionType.Zstd,
            _ => throw new RvzFormatException(
                $"Unknown compression method '{name}' (expected none, zstd, bzip2, lzma or lzma2)."),
        };
}
