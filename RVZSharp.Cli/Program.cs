using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Format;
using RVZSharp.Wii;

namespace RVZSharp.Cli;

/// <summary>
/// Command-line tool with the same command surface as Dolphin's DolphinTool:
/// convert, header, verify, extract (plus the legacy info/decode commands).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            return args[0] switch
            {
                "convert" => ConvertCommand(args[1..]),
                "header" => HeaderCommand(args[1..]),
                "verify" => VerifyCommand(args[1..]),
                "extract" => ExtractCommand(args[1..]),
                "info" when args.Length >= 2 => Info(args[1]),
                "decode" when args.Length >= 3 => Decode(args[1], args[2], args[3..]),
                _ => PrintUsageAndFail(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            usage: rvzsharp COMMAND -h

            commands supported: [convert, verify, header, extract]
            legacy commands:    [info, decode]

            convert  -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz] [-s]
                     [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>]
            header   -i <FILE> [-j] [-b] [-c] [-l]
            verify   -i <FILE> [-u <dir>] [-a crc32|md5|sha1]
            extract  -i <FILE> [-o <dir>] [-p <name>] [-s <path>] [-l <path>] [-q] [-g]
            info     <FILE>                        (legacy alias of 'header')
            decode   <FILE> <OUT> [--sha1 <hex>]   (decode any blob to a plain ISO)
            """);
    }

    private static int PrintUsageAndFail()
    {
        PrintUsage();
        return 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    // ------------------------------------------------------------------
    // Minimal optparse-style option parser (DolphinTool uses optparse).
    // ------------------------------------------------------------------

    private sealed record OptionSpec(string Short, bool TakesValue, string[]? Choices);

    private sealed class ParsedArgs
    {
        internal readonly Dictionary<string, string> _values = new();
        internal readonly HashSet<string> _flags = new();
        public List<string> Positionals { get; } = new();

        public bool IsSet(string longName) => _values.ContainsKey(longName) || _flags.Contains(longName);
        public bool HasFlag(string longName) => _flags.Contains(longName);
        public string? Get(string longName) => _values.GetValueOrDefault(longName);
    }

    private static ParsedArgs ParseArgs(IReadOnlyList<string> args,
        IReadOnlyDictionary<string, OptionSpec> spec)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string name;
            string? inlineValue = null;
            var takesValue = false;
            string[]? choices = null;

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                var longName = eq >= 0 ? arg[..eq] : arg;
                // Spec keys are dash-less long names ("input"); also accept the "--input"
                // form advertised in the usage text.
                if (!spec.TryGetValue(longName, out var option) &&
                    !spec.TryGetValue(longName[2..], out option))
                {
                    throw new CliError($"no such option: {arg}");
                }

                name = longName[2..];
                takesValue = option.TakesValue;
                choices = option.Choices;
                inlineValue = eq >= 0 ? arg[(eq + 1)..] : null;
            }
            else if (arg.Length > 1 && arg[0] == '-')
            {
                var match = spec.FirstOrDefault(pair => pair.Value.Short == arg);
                if (match.Key == null)
                {
                    throw new CliError($"no such option: {arg}");
                }

                name = match.Key;
                takesValue = match.Value.TakesValue;
                choices = match.Value.Choices;
            }
            else
            {
                result.Positionals.Add(arg);
                continue;
            }

            if (!takesValue)
            {
                result._flags.Add(name);
                continue;
            }

            string value = inlineValue ?? (i + 1 < args.Count ? args[++i] : string.Empty);
            if (value.Length == 0)
            {
                throw new CliError($"option {arg} requires an argument");
            }

            if (choices is { Length: > 0 } && !choices.Contains(value))
            {
                throw new CliError(
                    $"option {arg}: invalid choice: '{value}' (choose from {string.Join(", ", choices)})");
            }

            result._values[name] = value;
        }

        return result;
    }

    private sealed class CliError : Exception
    {
        public CliError(string message)
            : base(message)
        {
        }
    }

    // ------------------------------------------------------------------
    // convert — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> ConvertSpec = new()
    {
        ["user"] = new("-u", true, null),
        ["input"] = new("-i", true, null),
        ["output"] = new("-o", true, null),
        ["format"] = new("-f", true, ["iso", "gcz", "wia", "rvz"]),
        ["scrub"] = new("-s", false, null),
        ["block_size"] = new("-b", true, null),
        ["compression"] = new("-c", true, ["none", "zstd", "bzip2", "lzma", "lzma2"]),
        ["compression_level"] = new("-l", true, null),
        // RVZSharp extensions (accepted in flag mode too)
        ["chunk-size"] = new("--chunk-size", true, null),
        ["no-packing"] = new("--no-packing", false, null),
    };

    private static int ConvertCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine(
                "usage: convert [options]... [FILE]...\n"
                + "  -u, --user <dir>           user folder path (accepted for compatibility)\n"
                + "  -i, --input <FILE>         path to disc image FILE\n"
                + "  -o, --output <FILE>        path to the destination FILE\n"
                + "  -f, --format <format>      container format: iso, gcz, wia, rvz\n"
                + "  -s, --scrub                scrub junk data (not supported)\n"
                + "  -b, --block_size <int>     block size in bytes (required for GCZ/WIA/RVZ)\n"
                + "  -c, --compression <method> none, zstd, bzip2, lzma, lzma2\n"
                + "  -l, --compression_level    level of compression for the selected method");
            return args.Count == 0 ? 1 : 0;
        }

        // Legacy positional form: convert <input> <output> [options]
        if (!args[0].StartsWith('-'))
        {
            if (args.Count < 2)
            {
                return Fail("No input set");
            }

            return ConvertLegacy(args[0], args[1], args.Skip(2).ToArray());
        }

        ParsedArgs options;
        try
        {
            options = ParseArgs(args, ConvertSpec);
        }
        catch (CliError e)
        {
            return Fail(e.Message);
        }

        if (!options.IsSet("input"))
        {
            return Fail("No input set");
        }

        if (!options.IsSet("output"))
        {
            return Fail("No output set");
        }

        var format = options.Get("format");
        if (format is not ("iso" or "gcz" or "wia" or "rvz"))
        {
            return Fail("No output format set");
        }

        var inputPath = options.Get("input")!;
        var outputPath = options.Get("output")!;

        IBlobReader blob;
        try
        {
            var file = File.OpenRead(inputPath);
            blob = Blob.Open(file, filePath: inputPath, leaveOpen: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
        {
            return Fail("The input file could not be opened.");
        }

        using (blob)
        {
            var input = (IBlobReader)blob;
            if (format is "gcz" or "wia")
            {
                return Fail(
                    $"Converting to {format.ToUpperInvariant()} is not supported by this implementation (supported: iso, rvz).");
            }

            if (options.HasFlag("scrub"))
            {
                // Dolphin scrubs the input before converting (ConvertCommand.cpp:170-197).
                // Without a filesystem (FST) parser, scrubbing zeroes the data of non-game
                // Wii partitions (update/channel) — the safe subset of DiscScrubber.
                var scrubbed = ScrubbedBlob.Create(blob);
                if (scrubbed == null)
                {
                    return Fail("Unable to process disc image. Try again without --scrub.");
                }

                input = scrubbed;
                if (format == "rvz")
                {
                    Console.Error.WriteLine(
                        "Warning: Scrubbing an RVZ container does not offer significant space "
                        + "advantages. Continuing anyway.");
                }
                else if (format == "iso")
                {
                    Console.Error.WriteLine(
                        "Warning: Scrubbing does not save space when converting to ISO unless "
                        + "using external compression. Continuing anyway.");
                }
            }

            // --block_size
            var blockSize = 0;
            if (format is "gcz" or "wia" or "rvz")
            {
                var blockSizeArg = options.IsSet("block_size")
                    ? options.Get("block_size")
                    : options.Get("chunk-size"); // RVZSharp extension: alias for -b (bytes)
                if (blockSizeArg == null || !int.TryParse(blockSizeArg, out blockSize))
                {
                    return Fail("Block size must be set for GCZ/RVZ/WIA");
                }

                if (!IsDiscImageBlockSizeValid(blockSize, format))
                {
                    return Fail("Block size is not valid for this format");
                }

                if (blockSize < 0x8000 || blockSize > 0x200000)
                {
                    Console.Error.WriteLine(
                        "Warning: Block size is not ideal for performance. Continuing anyway.");
                }
            }

            // --compression / --compression_level
            var compression = CompressionType.Zstd;
            var level = 0;
            var packing = true;
            if (format == "rvz")
            {
                var compressionName = options.Get("compression");
                if (compressionName is null)
                {
                    return Fail("Compression method must be set for WIA or RVZ");
                }

                compression = ParseCompression(compressionName);
                if (compression == CompressionType.Purge)
                {
                    return Fail("Compression type is not supported for the container format");
                }

                if (compression == CompressionType.None)
                {
                    level = 0;
                }
                else
                {
                    if (!options.IsSet("compression_level") ||
                        !int.TryParse(options.Get("compression_level"), out level))
                    {
                        return Fail("Compression level must be set when compression type is not 'none'");
                    }

                    var (min, max) = GetAllowedCompressionLevels(compression);
                    if (level < min || level > max)
                    {
                        return Fail("Compression level not in acceptable range");
                    }
                }

                if (options.IsSet("no-packing"))
                {
                    packing = false;
                }
            }

            if (format == "iso")
            {
                return DecodeBlob(input, outputPath, expectedSha1: null);
            }

            var writeOptions = new RvzWriteOptions
            {
                Compression = compression,
                CompressionLevel = level,
                ChunkSize = blockSize,
                Packing = packing,
            };

            using var output = File.Create(outputPath);
            RvzWriter.Write(input, output, writeOptions);
            return 0;
        }
    }

    /// <summary>Legacy positional convert: rvzsharp convert &lt;in&gt; &lt;out&gt; [options].</summary>
    private static int ConvertLegacy(string inputPath, string outputPath, IReadOnlyList<string> args)
    {
        // Dolphin-style suggested defaults (level 5, 131072-byte chunks); -b/-c/-l are not
        // required in this legacy form, but provided values are validated like the flag form.
        var options = new RvzWriteOptions { CompressionLevel = 5, ChunkSize = 131072 };
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--compression" when i + 1 < args.Count:
                    options = options with { Compression = ParseCompression(args[++i]) };
                    break;
                case "--level" when i + 1 < args.Count:
                    options = options with { CompressionLevel = int.Parse(args[++i]) };
                    break;
                // Bytes, like -b (--chunk-size used to be KiB).
                case "--chunk-size" when i + 1 < args.Count:
                    options = options with { ChunkSize = int.Parse(args[++i]) };
                    break;
                case "--no-packing":
                    options = options with { Packing = false };
                    break;
            }
        }

        if (options.Compression == CompressionType.Purge)
        {
            return Fail("PURGE compression is not supported for RVZ files.");
        }

        var (min, max) = GetAllowedCompressionLevels(options.Compression);
        if (options.CompressionLevel < min || options.CompressionLevel > max)
        {
            return Fail("Compression level not in acceptable range");
        }

        if (options.ChunkSize < 0x8000 ||
            (options.ChunkSize < (int)WiaDisc.GroupSize && (options.ChunkSize & (options.ChunkSize - 1)) != 0) ||
            (options.ChunkSize > (int)WiaDisc.GroupSize && options.ChunkSize % (int)WiaDisc.GroupSize != 0))
        {
            return Fail("Block size is not valid for this format");
        }

        using var input = File.OpenRead(inputPath);
        using var blob = Blob.Open(input, filePath: inputPath, leaveOpen: true);
        using var output = File.Create(outputPath);
        RvzWriter.Write(blob, output, options);
        return 0;
    }

    private static bool IsDiscImageBlockSizeValid(int blockSize, string format) => format switch
    {
        // GCZ: block size "must" be a power of 2
        "gcz" => blockSize > 0 && (blockSize & (blockSize - 1)) == 0,
        // WIA: not less than the minimum (2 MiB), and a multiple of it
        "wia" => blockSize >= 0x200000 && blockSize % 0x200000 == 0,
        // RVZ: not smaller than 32 KiB; below 2 MiB must be a power of 2;
        // above 2 MiB must be a multiple of 2 MiB
        "rvz" => blockSize >= 0x8000 &&
                 (blockSize < 0x200000 ? (blockSize & (blockSize - 1)) == 0
                                       : blockSize % 0x200000 == 0),
        _ => false,
    };

    private static (int Min, int Max) GetAllowedCompressionLevels(CompressionType compression) =>
        compression switch
        {
            CompressionType.Bzip2 or CompressionType.Lzma or CompressionType.Lzma2 => (1, 9),
            // Dolphin's non-GUI CLI accepts ZSTD_minCLevel()..ZSTD_maxCLevel()
            // (WIABlob.cpp:68-75): negative levels select fast modes, 0 is the default.
            CompressionType.Zstd => (-131072, 22),
            _ => (0, -1),
        };

    private static CompressionType ParseCompression(string name) =>
        name.ToLowerInvariant() switch
        {
            "none" => CompressionType.None,
            "purge" => CompressionType.Purge,
            "bzip2" or "bzip" => CompressionType.Bzip2,
            "lzma" => CompressionType.Lzma,
            "lzma2" => CompressionType.Lzma2,
            "zstd" or "zstandard" => CompressionType.Zstd,
            _ => throw new CliError(
                $"unknown compression method '{name}' (expected none, zstd, bzip2, lzma or lzma2)"),
        };

    // ------------------------------------------------------------------
    // header — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> HeaderSpec = new()
    {
        ["input"] = new("-i", true, null),
        ["json"] = new("-j", false, null),
        ["block_size"] = new("-b", false, null),
        ["compression"] = new("-c", false, null),
        ["compression_level"] = new("-l", false, null),
    };

    private static int HeaderCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine(
                "usage: header [options]...\n"
                + "  -i, --input <FILE>   path to disc image FILE\n"
                + "  -j, --json           print the information as JSON\n"
                + "  -b, --block_size     print the block size of GCZ/WIA/RVZ formats\n"
                + "  -c, --compression    print the compression method of GCZ/WIA/RVZ formats\n"
                + "  -l, --compression_level  print the level of compression for WIA/RVZ formats");
            return args.Count == 0 ? 1 : 0;
        }

        ParsedArgs options;
        try
        {
            options = ParseArgs(args, HeaderSpec);
        }
        catch (CliError e)
        {
            return Fail(e.Message);
        }

        var inputPath = options.Get("input");
        if (string.IsNullOrEmpty(inputPath))
        {
            return Fail("No input set");
        }

        IBlobReader blob;
        try
        {
            var file = File.OpenRead(inputPath);
            blob = Blob.Open(file, filePath: inputPath, leaveOpen: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
        {
            return Fail("Unable to open disc image");
        }

        using (blob)
        {
            var volume = DiscVolumeInfo.TryRead(blob);

            var blockSize = blob.BlockSize;
            var compressionMethod = GetCompressionMethod(blob);
            var compressionLevel = GetCompressionLevel(blob);

            if (options.HasFlag("json"))
            {
                var json = new JsonObject();
                if (blockSize != 0)
                {
                    json["block_size"] = blockSize;
                }

                if (compressionMethod.Length > 0)
                {
                    json["compression_method"] = compressionMethod;
                }

                if (compressionLevel is not null)
                {
                    json["compression_level"] = compressionLevel;
                }

                if (volume is not null)
                {
                    json["internal_name"] = volume.InternalName;
                    if (volume.Revision is not null)
                    {
                        json["revision"] = volume.Revision.Value;
                    }

                    json["game_id"] = volume.GameId;
                    if (volume.TitleId is not null)
                    {
                        json["title_id"] = volume.TitleId.Value;
                    }

                    json["region"] = volume.Region;
                    json["country"] = volume.Country;
                }

                Console.WriteLine(json.ToJsonString());
                return 0;
            }

            if (options.HasFlag("block_size") || options.HasFlag("compression") ||
                options.HasFlag("compression_level"))
            {
                if (options.HasFlag("block_size"))
                {
                    Console.WriteLine(blockSize == 0 ? "N/A" : blockSize.ToString());
                }

                if (options.HasFlag("compression"))
                {
                    Console.WriteLine(compressionMethod.Length == 0 ? "N/A" : compressionMethod);
                }

                if (options.HasFlag("compression_level"))
                {
                    Console.WriteLine(compressionLevel?.ToString() ?? "N/A");
                }

                return 0;
            }

            // Full report.
            if (blockSize != 0)
            {
                Console.WriteLine($"Block Size: {blockSize}");
            }

            if (compressionMethod.Length > 0)
            {
                Console.WriteLine($"Compression Method: {compressionMethod}");
            }

            if (compressionLevel is not null)
            {
                Console.WriteLine($"Compression Level: {compressionLevel}");
            }

            if (volume is not null)
            {
                Console.WriteLine($"Internal Name: {volume.InternalName}");
                if (volume.Revision is not null)
                {
                    Console.WriteLine($"Revision: {volume.Revision}");
                }

                Console.WriteLine($"Game ID: {volume.GameId}");
                if (volume.TitleId is not null)
                {
                    Console.WriteLine($"Title ID: {volume.TitleId:X16}");
                }

                Console.WriteLine($"Region: {volume.Region}");
                Console.WriteLine($"Country: {volume.Country}");
            }

            return 0;
        }
    }

    private static string GetCompressionMethod(IBlobReader blob) => blob switch
    {
        RvzReader rvz => rvz.Disc.Compression switch
        {
            CompressionType.None => "",
            CompressionType.Purge => "Purge",
            CompressionType.Bzip2 => "bzip2",
            CompressionType.Lzma => "LZMA",
            CompressionType.Lzma2 => "LZMA2",
            CompressionType.Zstd => "Zstandard",
            _ => "",
        },
        GczBlob => "Deflate",
        _ => "",
    };

    private static int? GetCompressionLevel(IBlobReader blob) => blob switch
    {
        RvzReader rvz => rvz.Disc.ComprLevel,
        _ => null,
    };

    // ------------------------------------------------------------------
    // verify — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> VerifySpec = new()
    {
        ["user"] = new("-u", true, null),
        ["input"] = new("-i", true, null),
        // Dolphin only offers rchash when built with RetroAchievements support
        // (VerifyCommand.cpp:134-137); without it, -a rchash is an invalid choice.
        ["algorithm"] = new("-a", true, ["crc32", "md5", "sha1"]),
    };

    private static int VerifyCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine(
                "usage: verify [options]...\n"
                + "  -u, --user <dir>           user folder path (accepted for compatibility)\n"
                + "  -i, --input <FILE>         path to input file\n"
                + "  -a, --algorithm <algo>     compute one digest: crc32, md5, sha1");
            return args.Count == 0 ? 1 : 0;
        }

        ParsedArgs options;
        try
        {
            options = ParseArgs(args, VerifySpec);
        }
        catch (CliError e)
        {
            return Fail(e.Message);
        }

        if (!options.IsSet("input"))
        {
            return Fail("No input set");
        }

        var algorithm = options.Get("algorithm");

        var inputPath = options.Get("input")!;
        IBlobReader blob;
        try
        {
            var file = File.OpenRead(inputPath);
            blob = Blob.Open(file, filePath: inputPath, leaveOpen: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
        {
            return Fail("Unable to open input file");
        }

        using (blob)
        {
            // Dolphin's verify requires a GC/Wii volume (VerifyCommand.cpp:148-154): check
            // the disc magic (GC DVD magic at 0x1C, Wii magic at 0x18) so non-disc blobs
            // fail like Dolphin.
            Span<byte> discHeader = stackalloc byte[0x80];
            if (blob.ReadAt(0, discHeader) != discHeader.Length)
            {
                return Fail("The input file is not a GC/Wii disc.");
            }

            var wiiMagic = (uint)((discHeader[0x18] << 24) | (discHeader[0x19] << 16) |
                                  (discHeader[0x1A] << 8) | discHeader[0x1B]);
            var gcMagic = (uint)((discHeader[0x1C] << 24) | (discHeader[0x1D] << 16) |
                                 (discHeader[0x1E] << 8) | discHeader[0x1F]);
            if (wiiMagic != WiiVolume.WII_MAGIC && gcMagic != WiiVolume.GC_MAGIC)
            {
                return Fail("The input file is not a GC/Wii disc.");
            }

            var wantCrc32 = algorithm is null || algorithm == "crc32";
            var wantMd5 = algorithm is null || algorithm == "md5";
            var wantSha1 = algorithm is null || algorithm == "sha1";

            uint crc = 0xFFFFFFFF;
            var md5 = wantMd5 ? MD5.Create() : null;
            var sha1 = wantSha1 ? SHA1.Create() : null;

            var buffer = new byte[1 << 20];
            var position = 0L;
            while (position < blob.Length)
            {
                var read = blob.ReadAt(position, buffer);
                if (read <= 0)
                {
                    return Fail($"Verification stopped at offset 0x{position:X}.");
                }

                if (wantCrc32)
                {
                    crc = Crc32.Update(crc, buffer.AsSpan(0, read));
                }

                md5?.TransformBlock(buffer, 0, read, null, 0);
                sha1?.TransformBlock(buffer, 0, read, null, 0);
                position += read;
            }

            md5?.TransformFinalBlock([], 0, 0);
            sha1?.TransformFinalBlock([], 0, 0);
            crc ^= 0xFFFFFFFF;

            if (algorithm is not null)
            {
                Console.WriteLine(algorithm switch
                {
                    "crc32" => crc.ToString("x8"),
                    "md5" => ToLowerHex(md5!.Hash!),
                    _ => ToLowerHex(sha1!.Hash!),
                });
                return 0;
            }

            Console.WriteLine($"CRC32: {crc:x8}");
            Console.WriteLine($"MD5: {ToLowerHex(md5!.Hash!)}");
            Console.WriteLine($"SHA1: {ToLowerHex(sha1!.Hash!)}");
            return 0;
        }
    }

    private static string ToLowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>IEEE CRC-32 (as used by zlib / Dolphin's CRC32 hashes).</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }

    // ------------------------------------------------------------------
    // extract — DolphinTool-compatible surface (not implemented yet).
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> ExtractSpec = new()
    {
        ["input"] = new("-i", true, null),
        ["output"] = new("-o", true, null),
        ["partition"] = new("-p", true, null),
        ["single"] = new("-s", true, null),
        ["list"] = new("-l", false, null),
        ["quiet"] = new("-q", false, null),
        ["gameonly"] = new("-g", false, null),
    };

    private static int ExtractCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine(
                "usage: extract [options]...\n"
                + "  -i, --input <FILE>     path to disc image FILE\n"
                + "  -o, --output <dir>     output directory\n"
                + "  -p, --partition <name> extract only this partition\n"
                + "  -s, --single <path>    extract a single file\n"
                + "  -l, --list            list the files under this path\n"
                + "  -q, --quiet            do not print progress\n"
                + "  -g, --gameonly         only extract the main game partition");
            return args.Count == 0 ? 1 : 0;
        }

        ParsedArgs options;
        try
        {
            options = ParseArgs(args, ExtractSpec);
        }
        catch (CliError e)
        {
            return Fail(e.Message);
        }

        if (!options.IsSet("input"))
        {
            return Fail("No input set");
        }

        return Fail("The extract command is not supported by this implementation (no disc filesystem support yet).");
    }

    // ------------------------------------------------------------------
    // Disc volume info (the "game data" section of `header`).
    // Matches Dolphin's VolumeDisc field reads.
    // ------------------------------------------------------------------

    private sealed record DiscVolumeInfo(
        string GameId, byte? Revision, string InternalName, ulong? TitleId, string Region, string Country)
    {
        public static DiscVolumeInfo? TryRead(IBlobReader disc)
        {
            if (disc.Length < 0x80)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[0x80];
            if (disc.ReadAt(0, header) != header.Length)
            {
                return null;
            }

            var isWii = Be32(header, 0x18) == WiiVolume.WII_MAGIC;
            var isGc = Be32(header, 0x1C) == WiiVolume.GC_MAGIC;
            if (!isWii && !isGc)
            {
                return null;
            }

            var gameId = FilterGameId(header[..6]);
            var revision = header[7];

            var region = ReadRegion(disc, isWii);
            var internalName = DecodeInternalName(disc, region);
            var titleId = isWii ? ReadTitleId(disc) : null;
            var countryCode = gameId[3];
            var country = CountryCodeToCountry(countryCode, isWii, region, revision);
            // Dolphin falls back to the region's typical country when the country byte
            // contradicts the region (VolumeDisc.cpp:93-99).
            if (CountryCodeToRegion(countryCode, isWii, region, revision) != region)
            {
                country = TypicalCountryForRegion(region);
            }

            return new DiscVolumeInfo(gameId, revision, internalName, titleId, region, country);
        }

        /// <summary>
        /// 6 bytes at offset 0; any non-alphanumeric byte becomes '-', including NUL and
        /// the country byte (Dolphin: Volume.cpp:46-56).
        /// </summary>
        private static string FilterGameId(ReadOnlySpan<byte> id)
        {
            var chars = new char[id.Length];
            for (var i = 0; i < id.Length; i++)
            {
                var c = (char)id[i];
                chars[i] = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    ? c
                    : '-';
            }

            return new string(chars);
        }

        /// <summary>0x60 bytes at 0x20, up to the first NUL; CP1252, or Shift-JIS for NTSC-J
        /// (Dolphin: Volume.cpp:39-44).</summary>
        private static string DecodeInternalName(IBlobReader disc, string region)
        {
            var raw = new byte[0x60];
            if (disc.ReadAt(0x20, raw) != raw.Length)
            {
                return string.Empty;
            }

            var end = Array.IndexOf(raw, (byte)0);
            if (end < 0)
            {
                end = raw.Length;
            }

            return NameEncoding(region).GetString(raw, 0, end);
        }

        private static Encoding NameEncoding(string region) => region == "NTSC-J" ? ShiftJis : Cp1252;

        private static readonly Encoding Cp1252 = GetCodePageEncoding(1252);
        private static readonly Encoding ShiftJis = GetCodePageEncoding(932);

        private static Encoding GetCodePageEncoding(int codePage)
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage);
        }

        /// <summary>
        /// Title ID from the game partition's ticket (u64 BE at ticket + 0x1DC).
        /// The game partition is the one with type 0 in the partition table.
        /// </summary>
        private static ulong? ReadTitleId(IBlobReader disc)
        {
            try
            {
                Span<byte> value = stackalloc byte[8];
                foreach (var partition in WiiVolume.GetPartitions(disc))
                {
                    if (partition.Type != 0)
                    {
                        continue;
                    }

                    if (disc.ReadAt((long)partition.Offset + 0x1DC, value) == value.Length)
                    {
                        return Be64(value, 0);
                    }
                }
            }
            catch (RvzException)
            {
                // Unreadable partition table — no title ID.
            }

            return null;
        }

        private static string ReadRegion(IBlobReader disc, bool isWii)
        {
            // GC: region word at 0x458; Wii: region word at 0x4E000.
            var offset = isWii ? 0x4E000 : 0x458;
            if (disc.Length < offset + 4)
            {
                return "Unknown";
            }

            Span<byte> value = stackalloc byte[4];
            if (disc.ReadAt(offset, value) != value.Length)
            {
                return "Unknown";
            }

            var code = Be32(value, 0);
            return code switch
            {
                0 => "NTSC-J",
                1 => "NTSC-U",
                2 => "PAL",
                4 => "NTSC-K",
                _ => "Unknown",
            };
        }

        /// <summary>Dolphin's CountryCodeToRegion (Enums.cpp:213-268).</summary>
        private static string CountryCodeToRegion(char code, bool isWii, string region, byte revision)
        {
            var isGc = !isWii;
            switch (code)
            {
                case '\x02':
                    return region; // Wii Menu (same title ID for all regions)
                case 'J':
                    return "NTSC-J";
                case 'W':
                    // Only the Nordic version of Ratatouille (Wii) is PAL; otherwise Korean
                    // GC games in English or Taiwanese Wii games.
                    return region == "PAL" ? "PAL" : "NTSC-J";
                case 'E':
                    if (!isGc)
                    {
                        return "NTSC-U"; // the most common country code for NTSC-U
                    }

                    return revision >= 0x30 ? "NTSC-J" : "NTSC-U"; // Korean GC games in English
                case 'B':
                case 'N':
                    return "NTSC-U";
                case 'X':
                case 'Y':
                case 'Z':
                    // Additional language versions, store-exclusive versions, special versions.
                    return region == "NTSC-U" ? "NTSC-U" : "PAL";
                case 'D':
                case 'F':
                case 'H':
                case 'I':
                case 'L':
                case 'M':
                case 'P':
                case 'R':
                case 'S':
                case 'U':
                case 'V':
                    return "PAL";
                case 'K':
                case 'Q':
                case 'T':
                    // All Korean, but the NTSC-K region does not exist on GC.
                    return isGc ? "NTSC-J" : "NTSC-K";
                default:
                    return "Unknown";
            }
        }

        /// <summary>Dolphin's TypicalCountryForRegion (Enums.cpp:173-187).</summary>
        private static string TypicalCountryForRegion(string region) => region switch
        {
            "NTSC-J" => "Japan",
            "NTSC-U" => "USA",
            "PAL" => "Europe",
            "NTSC-K" => "Korea",
            _ => "Unknown",
        };

        /// <summary>Dolphin's CountryCodeToCountry (Enums.cpp).</summary>
        private static string CountryCodeToCountry(char code, bool isWii, string region, byte revision)
        {
            var isGc = !isWii;
            switch (code)
            {
                case 'A':
                    return "World";
                case 'X':
                case 'Y':
                case 'Z':
                    return region == "NTSC-U" ? "USA" : "Europe";
                case 'W':
                    if (isGc)
                    {
                        return "Korea";
                    }

                    return region == "PAL" ? "Europe" : "Taiwan";
                case 'D':
                    return "Germany";
                case 'L':
                case 'M':
                case 'V':
                case 'P':
                    return "Europe";
                case 'U':
                    return "Australia";
                case 'F':
                    return "France";
                case 'I':
                    return "Italy";
                case 'H':
                    return "Netherlands";
                case 'R':
                    return "Russia";
                case 'S':
                    return "Spain";
                case 'E':
                    if (!isGc)
                    {
                        return "USA";
                    }

                    if (revision >= 0x30)
                    {
                        return "Korea";
                    }

                    return region == "NTSC-J" ? "Korea" : "USA";
                case 'B':
                case 'N':
                    return "USA";
                case 'J':
                    return "Japan";
                case 'K':
                case 'Q':
                case 'T':
                    return "Korea";
                default:
                    return "Unknown";
            }
        }
    }

    private static uint Be32(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static ulong Be64(ReadOnlySpan<byte> data, int offset) =>
        ((ulong)Be32(data, offset) << 32) | Be32(data, offset + 4);

    // ------------------------------------------------------------------
    // Legacy commands: info / decode.
    // ------------------------------------------------------------------

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

    private static int Decode(string inputPath, string outputPath, IReadOnlyList<string> args)
    {
        string? expectedSha1 = null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--sha1")
            {
                expectedSha1 = args[i + 1];
            }
        }

        using var input = File.OpenRead(inputPath);
        using var reader = Blob.Open(input, filePath: inputPath, leaveOpen: true);
        return DecodeBlob(reader, outputPath, expectedSha1);
    }

    private static int DecodeBlob(IBlobReader reader, string outputPath, string? expectedSha1)
    {
        using var output = File.Create(outputPath);
        using var sha1 = SHA1.Create();

        var buffer = new byte[1 << 20];
        var position = 0L;
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
