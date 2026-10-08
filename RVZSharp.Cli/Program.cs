using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RVZSharp.Blobs;
using RVZSharp.Files;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Verification;
using RVZSharp.Wii;
using Serilog;
using RVZSharp.Cli.Logging;

namespace RVZSharp.Cli;

/// <summary>
/// Command-line tool with the same command surface as Dolphin's DolphinTool:
/// convert, header, verify, extract (plus the legacy info/decode commands).
/// </summary>
internal static class Program
{
    /// <summary>
    /// Cancellation source for the running command: Ctrl+C requests cancellation and the
    /// command observes the token between reads instead of the process dying mid-write.
    /// </summary>
    private static readonly CancellationTokenSource Cancellation = new();

    private static int Main(string[] args)
    {
        LogSetup.Initialize();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cancellation.Cancel();
        };

        // Usage telemetry: fire-and-forget hit at launch so application usage can
        // be tracked on the ApplicationStats dashboard. Failures are silent.
        var statsClient = new StatsApiClient();
        var statsReport = statsClient.ReportUsageAsync();

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
                "completions" when args.Length >= 2 => CompletionsCommand(args[1]),
                "info" when args.Length >= 2 => Info(args[1]),
                "decode" when args.Length >= 3 => Decode(args[1], args[2], args[3..]),
                _ => PrintUsageAndFail()
            };
        }
        catch (OperationCanceledException)
        {
            ConsoleProgress.Clear();
            Console.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception e)
        {
            Log.Error(e, "Unhandled exception in Main");
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
        finally
        {
            // Best-effort: give the launch hit a moment to reach the API before exiting.
            statsReport.Wait(TimeSpan.FromSeconds(3));
            statsClient.Dispose();
            LogSetup.Shutdown();
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
                                usage: rvzsharp COMMAND -h

                                commands supported: [convert, verify, header, extract]
                                legacy commands:    [info, decode]
                                helpers:            [completions]

                                convert  -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz|ciso|wbfs|tgc] [-s]
                                         [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2|purge] [-l <level>]
                                         [--threads <int>] [--verify] [--json]
                                header   -i <FILE> [-j] [-b] [-c] [-l]
                                verify   -i <FILE> [-u <dir>] [-a crc32|md5|sha1] [--partitions] [--json]
                                extract  -i <FILE> [-o <dir>] [-p <name>] [-s <path>] [-l] [-q] [-g]
                                info     <FILE>                        (legacy alias of 'header')
                                decode   <FILE> <OUT> [--sha1 <hex>] [--threads <int>] (decode any blob to a plain ISO)
                                completions <bash|zsh|fish|powershell>  print a shell completion script

                                FILE arguments may be '-' to read the disc image from stdin; convert also
                                accepts '-o -' to write the converted image to stdout.
                                """);
    }

    private static int PrintUsageAndFail()
    {
        PrintUsage();
        return 1;
    }

    /// <summary>Prints the shell completion script for bash, zsh, fish or PowerShell.</summary>
    private static int CompletionsCommand(string shell)
    {
        var script = Completions.Get(shell);
        if (script is null)
        {
            return Fail($"unsupported shell '{shell}' (expected bash, zsh, fish or powershell)");
        }

        Console.WriteLine(script);
        return 0;
    }

    private static int Fail(string message)
    {
        ConsoleProgress.Clear();
        Log.Warning("Command failed: {Message}", message);
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    /// <summary>
    /// Progress reporter that updates a single line on stderr. Nothing is written when
    /// stderr is redirected, so scripts and logs stay clean.
    /// </summary>
    private sealed class ConsoleProgress : IProgress<double>
    {
        private readonly string _label;

        public ConsoleProgress(string label)
        {
            _label = label;
        }

        public void Report(double value)
        {
            if (!Console.IsErrorRedirected)
            {
                Console.Error.Write($"\r{_label}{value,6:P1}");
            }
        }

        /// <summary>Erases the progress line before a result is printed.</summary>
        public static void Clear()
        {
            if (!Console.IsErrorRedirected)
            {
                Console.Error.Write("\r" + new string(' ', 40) + "\r");
            }
        }
    }

    /// <summary>Write-through stream that hashes everything written to it (SHA-1).</summary>
    private sealed class HashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        public HashingStream(Stream inner)
        {
            _inner = inner;
        }

        public byte[] GetHashAndReset()
        {
            return _hash.GetHashAndReset();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Temporary file backing the <c>-</c> stdin/stdout convention: stdin is spooled to a
    /// seekable temp file because every container reader/writer does random access, and an
    /// output image is written to a temp file and copied to stdout after verification.
    /// </summary>
    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; }

        private TemporaryFile(string path)
        {
            Path = path;
        }

        /// <summary>Creates a unique, not-yet-existing temp file path.</summary>
        public static TemporaryFile Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RVZSharp");
            Directory.CreateDirectory(directory);
            return new TemporaryFile(
                System.IO.Path.Combine(directory, $"cli-{Guid.NewGuid():N}.tmp"));
        }

        /// <summary>Spools the entire standard input into a seekable temp file.</summary>
        public static TemporaryFile SpoolStdin()
        {
            var file = Create();
            using var stdin = Console.OpenStandardInput();
            using var output = File.Create(file.Path);
            stdin.CopyTo(output);

            return file;
        }

        public void CopyToStdout()
        {
            using var stdout = Console.OpenStandardOutput();
            using var file = File.OpenRead(Path);
            file.CopyTo(stdout);
            stdout.Flush();
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // The OS cleans the temp directory up eventually.
            }
        }
    }

    // ------------------------------------------------------------------
    // Minimal optparse-style option parser (DolphinTool uses optparse).
    // ------------------------------------------------------------------

    private sealed record OptionSpec(string Short, bool TakesValue, string[]? Choices);

    private sealed class ParsedArgs
    {
        internal readonly Dictionary<string, string> Values = new();
        internal readonly HashSet<string> Flags = new();
        public List<string> Positionals { get; } = new();

        public bool IsSet(string longName)
        {
            return Values.ContainsKey(longName) || Flags.Contains(longName);
        }

        public bool HasFlag(string longName)
        {
            return Flags.Contains(longName);
        }

        public string? Get(string longName)
        {
            return Values.GetValueOrDefault(longName);
        }
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
                    throw new CliErrorException($"no such option: {arg}");
                }

                name = longName[2..];
                takesValue = option.TakesValue;
                choices = option.Choices;
                inlineValue = eq >= 0 ? arg[(eq + 1)..] : null;
            }
            else if (arg.Length > 1 && arg[0] == '-')
            {
                var match = spec.FirstOrDefault(pair => pair.Value.Short == arg);

                name = match.Key ?? throw new CliErrorException($"no such option: {arg}");
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
                result.Flags.Add(name);
                continue;
            }

            string value = inlineValue ?? (i + 1 < args.Count ? args[++i] : string.Empty);
            if (value.Length == 0)
            {
                throw new CliErrorException($"option {arg} requires an argument");
            }

            if (choices is { Length: > 0 } && !choices.Contains(value))
            {
                throw new CliErrorException(
                    $"option {arg}: invalid choice: '{value}' (choose from {string.Join(", ", choices)})");
            }

            result.Values[name] = value;
        }

        return result;
    }

    [SuppressMessage("Roslynator", "RCS1194", Justification = "Only the message constructor is used.")]
    private sealed class CliErrorException : Exception
    {
        public CliErrorException(string message)
            : base(message)
        {
        }
    }

    // ------------------------------------------------------------------
    // convert — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> ConvertSpec = new()
    {
        ["user"] = new OptionSpec("-u", true, null),
        ["input"] = new OptionSpec("-i", true, null),
        ["output"] = new OptionSpec("-o", true, null),
        ["format"] = new OptionSpec("-f", true, ["iso", "gcz", "wia", "rvz", "ciso", "wbfs", "tgc"]),
        ["scrub"] = new OptionSpec("-s", false, null),
        ["block_size"] = new OptionSpec("-b", true, null),
        ["compression"] = new OptionSpec("-c", true, ["none", "zstd", "bzip2", "lzma", "lzma2", "purge"]),
        ["compression_level"] = new OptionSpec("-l", true, null),
        // RVZSharp extensions (accepted in flag mode too)
        ["chunk-size"] = new OptionSpec("--chunk-size", true, null),
        ["no-packing"] = new OptionSpec("--no-packing", false, null),
        ["threads"] = new OptionSpec("--threads", true, null),
        ["verify"] = new OptionSpec("--verify", false, null),
        ["json"] = new OptionSpec("--json", false, null)
    };

    private static int ConvertCommand(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
            {
                Console.Error.WriteLine(
                    "usage: convert [options]... [FILE]...\n"
                    + "  -u, --user <dir>           user folder path (accepted for compatibility)\n"
                    + "  -i, --input <FILE>         path to disc image FILE\n"
                    + "  -o, --output <FILE>        path to the destination FILE\n"
                    + "  -f, --format <format>      container format: iso, gcz, wia, rvz, ciso, wbfs, tgc\n"
                    + "  -s, --scrub                scrub junk data as part of conversion\n"
                    + "  -b, --block_size <int>     block size in bytes (required for GCZ/WIA/RVZ)\n"
                    + "  -c, --compression <method> none, zstd, bzip2, lzma, lzma2\n"
                    + "  -l, --compression_level    level of compression for the selected method\n"
                    + "  --threads <int>            compression/decode threads (0 = processor count, default)\n"
                    + "  --verify                   verify the written file decodes to the input image\n"
                    + "  --json                     print the conversion result as JSON on stdout\n"
                    + "\n"
                    + "  -i - reads the input from stdin; -o - writes the image to stdout (no --json).");
                return args.Count == 0 ? 1 : 0;
            }

            // Legacy positional form: convert <input> <output> [options]
            if (!args[0].StartsWith('-'))
            {
                if (args.Count < 2)
                {
                    return Fail("No input set");
                }

                using var legacyInput = args[0] == "-" ? TemporaryFile.SpoolStdin() : null;
                using var legacyOutput = args[1] == "-" ? TemporaryFile.Create() : null;
                var legacyResult = ConvertLegacy(legacyInput?.Path ?? args[0],
                    legacyOutput?.Path ?? args[1], args.Skip(2).ToArray());
                if (legacyResult == 0 && legacyOutput != null)
                {
                    legacyOutput.CopyToStdout();
                }

                return legacyResult;
            }

            ParsedArgs options;
            try
            {
                options = ParseArgs(args, ConvertSpec);
            }
            catch (CliErrorException e)
            {
                Log.Warning(e, "CLI parse error in ConvertCommand");
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
            if (format is not ("iso" or "gcz" or "wia" or "rvz" or "ciso" or "wbfs" or "tgc"))
            {
                return Fail("No output format set");
            }

            var originalInput = options.Get("input")!;
            var originalOutput = options.Get("output")!;
            var json = options.HasFlag("json");

            if (json && originalOutput == "-")
            {
                return Fail("--json cannot be combined with writing the image to stdout");
            }

            using var spooledInput = originalInput == "-" ? TemporaryFile.SpoolStdin() : null;
            using var spooledOutput = originalOutput == "-" ? TemporaryFile.Create() : null;
            var inputPath = spooledInput?.Path ?? originalInput;
            var writePath = spooledOutput?.Path ?? originalOutput;

            IBlobReader blob;
            try
            {
                var file = File.OpenRead(inputPath);
                blob = Blob.Open(file, filePath: inputPath, leaveOpen: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
            {
                Log.Error(e, "Failed to open input file '{InputPath}'", inputPath);
                return Fail("The input file could not be opened.");
            }

            using (blob)
            {
                var input = blob;
                if (options.HasFlag("scrub"))
                {
                    var scrubbed = ScrubbedBlob.Create(blob);
                    if (scrubbed == null)
                    {
                        return Fail("Unable to process disc image. Try again without --scrub.");
                    }

                    input = scrubbed;
                    switch (format)
                    {
                        case "rvz":
                            Log.Warning(
                                "Scrubbing an RVZ container does not offer significant space advantages. Continuing anyway.");
                            break;
                        case "iso":
                            Log.Warning(
                                "Scrubbing does not save space when converting to ISO unless using external compression. Continuing anyway.");
                            break;
                    }
                }

                // --verify: hash the (possibly scrubbed) input first, then the written file.
                DiscHashes? inputHashes = null;
                if (options.HasFlag("verify"))
                {
                    var verifyProgress = new ConsoleProgress("Verifying ");
                    inputHashes = DiscHasher.Compute(input, verifyProgress, Cancellation.Token);
                    ConsoleProgress.Clear();
                }

                var maxThreads = 0;
                if (options.IsSet("threads") &&
                    (!int.TryParse(options.Get("threads"), out maxThreads) || maxThreads < 0))
                {
                    return Fail("Threads must be a non-negative integer (0 = processor count)");
                }

                if (format == "iso")
                {
                    var decodeResult = DecodeBlob(input, writePath, expectedSha1: null, maxThreads,
                        originalOutput, quiet: spooledOutput != null);
                    return decodeResult != 0
                        ? decodeResult
                        : FinishConvert(originalInput, originalOutput, writePath, format, input.Length,
                            inputHashes, json, spooledOutput);
                }

                var blockSize = 0;
                if (format is "gcz" or "wia" or "rvz" or "ciso" or "wbfs")
                {
                    var blockSizeArg = options.IsSet("block_size")
                        ? options.Get("block_size")
                        : options.Get("chunk-size");

                    // CISO and WBFS have a natural default (2 MiB blocks/clusters); the RVZSharp
                    // writers are the ones that benefit from an explicit -b for the others.
                    if (blockSizeArg == null && (format is "ciso" or "wbfs"))
                    {
                        blockSize = 0x200000;
                    }
                    else if (blockSizeArg == null || !int.TryParse(blockSizeArg, out blockSize))
                    {
                        return Fail("Block size must be set for GCZ/RVZ/WIA");
                    }

                    if (!IsDiscImageBlockSizeValid(blockSize, format))
                    {
                        return Fail("Block size is not valid for this format");
                    }

                    if (blockSize is < 0x8000 or > 0x200000)
                    {
                        Log.Warning("Block size is not ideal for performance. Continuing anyway.");
                    }
                }

                var compression = CompressionType.Zstd;
                var level = 0;
                var packing = true;
                switch (format)
                {
                    case "rvz":
                    case "wia":
                    {
                        var compressionName = options.Get("compression");
                        if (compressionName is null)
                        {
                            return Fail("Compression method must be set for WIA or RVZ");
                        }

                        compression = ParseCompression(compressionName);
                        if ((format == "rvz" && compression == CompressionType.Purge) ||
                            (format == "wia" && compression == CompressionType.Zstd))
                        {
                            return Fail("Compression type is not supported for the container format");
                        }

                        // PURGE has no level (it stores hash exceptions plus a raw stream) and
                        // is a WIA-only method; the CLI exposes it as an RVZSharp extension.
                        if (compression is CompressionType.None or CompressionType.Purge)
                        {
                            level = 0;
                        }
                        else
                        {
                            if (!options.IsSet("compression_level") ||
                                !int.TryParse(options.Get("compression_level"), out level))
                            {
                                return Fail(
                                    "Compression level must be set when compression type is not 'none' or 'purge'");
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

                        break;
                    }
                }

                var writeOptions = new RvzWriteOptions
                {
                    Compression = compression,
                    CompressionLevel = level,
                    ChunkSize = blockSize,
                    Packing = packing,
                    MaxThreads = maxThreads
                };

                using (var output = File.Create(writePath))
                {
                    var progress = new ConsoleProgress("Encoding ");
                    try
                    {
                        if (format == "gcz")
                        {
                            if (!options.HasFlag("scrub") &&
                                Blob.GetDiscType(input) == DiscType.Wii)
                            {
                                Log.Warning(
                                    "Converting Wii disc images to GCZ without scrubbing may not "
                                    + "offer space advantages over ISO. Continuing anyway.");
                            }

                            GczWriter.Write(input, output, new GczWriteOptions
                            {
                                BlockSize = blockSize,
                                MaxThreads = maxThreads
                            }, progress, Cancellation.Token);
                        }
                        else if (format == "ciso")
                        {
                            CisoWriter.Write(input, output, new CisoWriteOptions
                            {
                                BlockSize = blockSize
                            }, progress, Cancellation.Token);
                        }
                        else if (format == "wbfs")
                        {
                            WbfsWriter.Write(input, output, new WbfsWriteOptions
                            {
                                BlockSize = blockSize
                            }, progress, Cancellation.Token);
                        }
                        else if (format == "tgc")
                        {
                            TgcWriter.Write(input, output, progress, Cancellation.Token);
                        }
                        else if (format == "wia")
                        {
                            WiaWriter.Write(input, output, writeOptions, progress, Cancellation.Token);
                        }
                        else
                        {
                            RvzWriter.Write(input, output, writeOptions, progress, Cancellation.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        ConsoleProgress.Clear();
                        Console.Error.WriteLine("Canceled.");
                        return 130;
                    }
                }

                ConsoleProgress.Clear();
                return FinishConvert(originalInput, originalOutput, writePath, format, input.Length,
                    inputHashes, json, spooledOutput);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Convert command failed");
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// Shared convert tail: optionally verify the written image, copy it to stdout when the
    /// output is '-', and print the JSON result. Returns the process exit code.
    /// </summary>
    private static int FinishConvert(string originalInput, string originalOutput, string writePath,
        string format, long inputLength, DiscHashes? inputHashes, bool json, TemporaryFile? spooledOutput)
    {
        var result = inputHashes is null
            ? 0
            : VerifyOutput(inputHashes, writePath, inputLength,
                quiet: json || spooledOutput != null);
        if (result != 0)
        {
            return result;
        }

        var outputSize = new FileInfo(writePath).Length;
        spooledOutput?.CopyToStdout();

        if (json)
        {
            var resultJson = new JsonObject
            {
                ["input"] = originalInput,
                ["output"] = originalOutput,
                ["format"] = format,
                ["input_bytes"] = inputLength,
                ["output_bytes"] = outputSize
            };
            if (inputHashes is not null)
            {
                resultJson["verified"] = true;
                resultJson["sha1"] = ToLowerHex(inputHashes.Sha1);
            }

            Console.WriteLine(resultJson.ToJsonString());
        }

        return 0;
    }

    /// <summary>Legacy positional convert: rvzsharp convert &lt;in&gt; &lt;out&gt; [options].</summary>
    private static int ConvertLegacy(string inputPath, string outputPath, IReadOnlyList<string> args)
    {
        try
        {
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
                    case "--chunk-size" when i + 1 < args.Count:
                        options = options with { ChunkSize = int.Parse(args[++i]) };
                        break;
                    case "--no-packing":
                        options = options with { Packing = false };
                        break;
                    case "--threads" when i + 1 < args.Count:
                        options = options with { MaxThreads = int.Parse(args[++i]) };
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
            var progress = new ConsoleProgress("Encoding ");
            try
            {
                RvzWriter.Write(blob, output, options, progress, Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                ConsoleProgress.Clear();
                Console.Error.WriteLine("Canceled.");
                return 130;
            }

            ConsoleProgress.Clear();
            return 0;
        }
        catch (Exception e)
        {
            Log.Error(e, "ConvertLegacy command failed");
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// Reopens the written file and checks that it decodes to the same CRC-32/MD5/SHA-1 as
    /// the input (the convert command's --verify extension).
    /// </summary>
    private static int VerifyOutput(DiscHashes inputHashes, string outputPath, long inputLength,
        bool quiet = false)
    {
        IBlobReader blob;
        try
        {
            var file = File.OpenRead(outputPath);
            blob = Blob.Open(file, filePath: outputPath, leaveOpen: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
        {
            Log.Error(e, "Failed to open output file '{OutputPath}' for verification", outputPath);
            return Fail("The output file could not be opened for verification.");
        }

        using (blob)
        {
            if (!Blob.IsDisc(blob))
            {
                return Fail("Verification failed: the output file is not a GC/Wii disc image.");
            }

            if (blob.Length < inputLength)
            {
                return Fail("Verification failed: the output image is smaller than the input.");
            }

            // CISO/WBFS decode to a padded image (map capacity / fixed Wii size); only the
            // first inputLength bytes correspond to the input image, so hash the prefix.
            var progress = new ConsoleProgress("Verifying ");
            var outputHashes = DiscHasher.Compute(blob, inputLength, progress, Cancellation.Token);
            ConsoleProgress.Clear();

            if (!inputHashes.Matches(outputHashes))
            {
                Console.Error.WriteLine($"  input  SHA1: {ToLowerHex(inputHashes.Sha1)}");
                Console.Error.WriteLine($"  output SHA1: {ToLowerHex(outputHashes.Sha1)}");
                return Fail("Verification failed: the output does not decode to the input image.");
            }

            if (!quiet)
            {
                Console.WriteLine($"Verification: OK ({ToLowerHex(inputHashes.Sha1)})");
            }

            return 0;
        }
    }

    private static bool IsDiscImageBlockSizeValid(int blockSize, string format)
    {
        return format switch
        {
            // GCZ/CISO: block size "must" be a power of 2
            "gcz" or "ciso" => blockSize > 0 && (blockSize & (blockSize - 1)) == 0,
            // WBFS: clusters are a power of 2 of at least 32 KiB (the u16 map fits a Wii disc)
            "wbfs" => blockSize >= 0x8000 && (blockSize & (blockSize - 1)) == 0,
            // WIA: not less than the minimum (2 MiB), and a multiple of it
            "wia" => blockSize >= 0x200000 && blockSize % 0x200000 == 0,
            // RVZ: not smaller than 32 KiB; below 2 MiB must be a power of 2;
            // above 2 MiB must be a multiple of 2 MiB
            "rvz" => blockSize >= 0x8000 &&
                     (blockSize < 0x200000
                         ? (blockSize & (blockSize - 1)) == 0
                         : blockSize % 0x200000 == 0),
            _ => false
        };
    }

    private static (int Min, int Max) GetAllowedCompressionLevels(CompressionType compression)
    {
        return compression switch
        {
            CompressionType.Bzip2 or CompressionType.Lzma or CompressionType.Lzma2 => (1, 9),
            // Dolphin's non-GUI CLI accepts ZSTD_minCLevel()..ZSTD_maxCLevel()
            // (WIABlob.cpp:68-75): negative levels select fast modes, 0 is the default.
            CompressionType.Zstd => (-131072, 22),
            _ => (0, -1)
        };
    }

    private static CompressionType ParseCompression(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "none" => CompressionType.None,
            "purge" => CompressionType.Purge,
            "bzip2" or "bzip" => CompressionType.Bzip2,
            "lzma" => CompressionType.Lzma,
            "lzma2" => CompressionType.Lzma2,
            "zstd" or "zstandard" => CompressionType.Zstd,
            _ => throw new CliErrorException(
                $"unknown compression method '{name}' (expected none, zstd, bzip2, lzma or lzma2)")
        };
    }

    // ------------------------------------------------------------------
    // header — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> HeaderSpec = new()
    {
        ["input"] = new OptionSpec("-i", true, null),
        ["json"] = new OptionSpec("-j", false, null),
        ["block_size"] = new OptionSpec("-b", false, null),
        ["compression"] = new OptionSpec("-c", false, null),
        ["compression_level"] = new OptionSpec("-l", false, null)
    };

    private static int HeaderCommand(IReadOnlyList<string> args)
    {
        try
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
            catch (CliErrorException e)
            {
                Log.Warning(e, "CLI parse error in HeaderCommand");
                return Fail(e.Message);
            }

            var inputPath = options.Get("input");
            if (string.IsNullOrEmpty(inputPath))
            {
                return Fail("No input set");
            }

            using var spooledInput = inputPath == "-" ? TemporaryFile.SpoolStdin() : null;
            var path = spooledInput?.Path ?? inputPath;

            IBlobReader blob;
            try
            {
                var file = File.OpenRead(path);
                blob = Blob.Open(file, filePath: path, leaveOpen: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
            {
                Log.Error(e, "Failed to open input file '{InputPath}'", path);
                return Fail("Unable to open disc image");
            }

            using (blob)
            {
                var volume = DiscInfo.TryRead(blob);

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
                        json["revision"] = volume.Revision;
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
                    Console.WriteLine($"Revision: {volume.Revision}");
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
        catch (Exception e)
        {
            Log.Error(e, "Header command failed");
            return Fail(e.Message);
        }
    }

    private static string GetCompressionMethod(IBlobReader blob)
    {
        return blob switch
        {
            RvzReader rvz => rvz.Disc.Compression switch
            {
                CompressionType.None => "",
                CompressionType.Purge => "Purge",
                CompressionType.Bzip2 => "bzip2",
                CompressionType.Lzma => "LZMA",
                CompressionType.Lzma2 => "LZMA2",
                CompressionType.Zstd => "Zstandard",
                _ => ""
            },
            GczBlob => "Deflate",
            _ => ""
        };
    }

    private static int? GetCompressionLevel(IBlobReader blob)
    {
        return blob switch
        {
            RvzReader rvz => rvz.Disc.ComprLevel,
            _ => null
        };
    }

    // ------------------------------------------------------------------
    // verify — DolphinTool-compatible.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> VerifySpec = new()
    {
        ["user"] = new OptionSpec("-u", true, null),
        ["input"] = new OptionSpec("-i", true, null),
        // Dolphin only offers rchash when built with RetroAchievements support
        // (VerifyCommand.cpp:134-137); without it, -a rchash is an invalid choice.
        ["algorithm"] = new OptionSpec("-a", true, ["crc32", "md5", "sha1"]),
        // RVZSharp extension: walk the Wii hash trees like Dolphin's verify tab.
        ["partitions"] = new OptionSpec("--partitions", false, null),
        ["json"] = new OptionSpec("--json", false, null)
    };

    private static int VerifyCommand(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
            {
                Console.Error.WriteLine(
                    "usage: verify [options]...\n"
                    + "  -u, --user <dir>           user folder path (accepted for compatibility)\n"
                    + "  -i, --input <FILE>         path to input file\n"
                    + "  -a, --algorithm <algo>     compute one digest: crc32, md5, sha1\n"
                    + "  --partitions               verify the Wii partition hash trees and TMD/H3 tables\n"
                    + "  --json                     print the result as JSON on stdout\n"
                    + "\n"
                    + "  -i - reads the input from stdin.");
                return args.Count == 0 ? 1 : 0;
            }

            ParsedArgs options;
            try
            {
                options = ParseArgs(args, VerifySpec);
            }
            catch (CliErrorException e)
            {
                Log.Warning(e, "CLI parse error in VerifyCommand");
                return Fail(e.Message);
            }

            if (!options.IsSet("input"))
            {
                return Fail("No input set");
            }

            var algorithm = options.Get("algorithm");
            var json = options.HasFlag("json");

            var inputPath = options.Get("input")!;
            using var spooledInput = inputPath == "-" ? TemporaryFile.SpoolStdin() : null;
            var path = spooledInput?.Path ?? inputPath;

            IBlobReader blob;
            try
            {
                var file = File.OpenRead(path);
                blob = Blob.Open(file, filePath: path, leaveOpen: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
            {
                Log.Error(e, "Failed to open input file '{InputPath}'", path);
                return Fail("Unable to open input file");
            }

            using (blob)
            {
                if (!Blob.IsDisc(blob))
                {
                    return Fail("The input file is not a GC/Wii disc.");
                }

                if (options.HasFlag("partitions"))
                {
                    var verifyProgress = new ConsoleProgress("Verifying ");
                    var report = DiscVerifier.Verify(blob, verifyProgress, Cancellation.Token);
                    ConsoleProgress.Clear();
                    return PrintVerificationReport(report, json, inputPath);
                }

                var progress = new ConsoleProgress("Verifying ");
                var hashes = DiscHasher.Compute(blob, progress, Cancellation.Token);
                ConsoleProgress.Clear();

                if (json)
                {
                    var result = new JsonObject { ["input"] = inputPath };
                    if (algorithm is not null)
                    {
                        result["algorithm"] = algorithm;
                        result["value"] = algorithm switch
                        {
                            "crc32" => hashes.Crc32.ToString("x8"),
                            "md5" => ToLowerHex(hashes.Md5),
                            _ => ToLowerHex(hashes.Sha1)
                        };
                    }
                    else
                    {
                        result["crc32"] = hashes.Crc32.ToString("x8");
                        result["md5"] = ToLowerHex(hashes.Md5);
                        result["sha1"] = ToLowerHex(hashes.Sha1);
                    }

                    Console.WriteLine(result.ToJsonString());
                    return 0;
                }

                if (algorithm is not null)
                {
                    Console.WriteLine(algorithm switch
                    {
                        "crc32" => hashes.Crc32.ToString("x8"),
                        "md5" => ToLowerHex(hashes.Md5),
                        _ => ToLowerHex(hashes.Sha1)
                    });
                    return 0;
                }

                Console.WriteLine($"CRC32: {hashes.Crc32:x8}");
                Console.WriteLine($"MD5: {ToLowerHex(hashes.Md5)}");
                Console.WriteLine($"SHA1: {ToLowerHex(hashes.Sha1)}");
                return 0;
            }
        }
        catch (OperationCanceledException)
        {
            ConsoleProgress.Clear();
            Console.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception e)
        {
            Log.Error(e, "Verify command failed");
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// Prints a <see cref="DiscVerifier"/> report (text or JSON) and returns 0 when the disc
    /// is valid, 1 otherwise (the verify command's --partitions extension).
    /// </summary>
    private static int PrintVerificationReport(VerificationReport report, bool json, string inputPath)
    {
        if (json)
        {
            var partitions = new JsonArray();
            foreach (var partition in report.Partitions)
            {
                partitions.Add((JsonNode)new JsonObject
                {
                    ["name"] = partition.Name,
                    ["offset"] = partition.Partition.Offset,
                    ["valid"] = partition.IsValid,
                    ["blocks"] = partition.Blocks,
                    ["verified_blocks"] = partition.VerifiedBlocks,
                    ["failed_blocks"] = partition.FailedBlocks,
                    ["issues"] = IssuesToJson(partition.Issues)
                });
            }

            var result = new JsonObject
            {
                ["input"] = inputPath,
                ["disc_type"] = report.DiscType.ToString(),
                ["valid"] = report.IsValid,
                ["total_blocks"] = report.TotalBlocks,
                ["verified_blocks"] = report.VerifiedBlocks,
                ["partitions"] = partitions,
                ["issues"] = IssuesToJson(report.Issues)
            };
            Console.WriteLine(result.ToJsonString());
            return report.IsValid ? 0 : 1;
        }

        Console.WriteLine($"Disc type: {report.DiscType}");
        if (report.TotalBlocks > 0)
        {
            Console.WriteLine($"Blocks verified: {report.VerifiedBlocks} of {report.TotalBlocks}");
        }

        foreach (var partition in report.Partitions)
        {
            Console.WriteLine(
                $"{partition.Name} partition at 0x{partition.Partition.Offset:X8}: "
                + $"{(partition.IsValid ? "OK" : "PROBLEMS")} "
                + $"({partition.VerifiedBlocks} blocks verified, {partition.FailedBlocks} failed)");
            foreach (var issue in partition.Issues)
            {
                Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
            }
        }

        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"[{issue.Severity}] {issue.Message}");
        }

        Console.WriteLine(report.IsValid ? "Verification OK." : "Verification failed.");
        return report.IsValid ? 0 : 1;
    }

    private static JsonArray IssuesToJson(IReadOnlyList<VerificationIssue> issues)
    {
        var array = new JsonArray();
        foreach (var issue in issues)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["severity"] = issue.Severity.ToString(),
                ["message"] = issue.Message
            });
        }

        return array;
    }

    private static string ToLowerHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // extract — DolphinTool-compatible surface.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, OptionSpec> ExtractSpec = new()
    {
        ["input"] = new OptionSpec("-i", true, null),
        ["output"] = new OptionSpec("-o", true, null),
        ["partition"] = new OptionSpec("-p", true, null),
        ["single"] = new OptionSpec("-s", true, null),
        ["list"] = new OptionSpec("-l", false, null),
        ["quiet"] = new OptionSpec("-q", false, null),
        ["gameonly"] = new OptionSpec("-g", false, null)
    };

    private static int ExtractCommand(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
            {
                Console.Error.WriteLine(
                    "usage: extract [options]...\n"
                    + "  -i, --input <FILE>     path to disc image FILE\n"
                    + "  -o, --output <path>    output directory (or list file with --list)\n"
                    + "  -p, --partition <name> extract only this partition (DATA/UPDATE/CHANNEL/...)\n"
                    + "  -s, --single <path>    extract (or list) only this file/directory\n"
                    + "  -l, --list             list the files instead of extracting them\n"
                    + "  -q, --quiet            do not print per-file progress\n"
                    + "  -g, --gameonly         only extract the DATA partition");
                return args.Count == 0 ? 1 : 0;
            }

            ParsedArgs options;
            try
            {
                options = ParseArgs(args, ExtractSpec);
            }
            catch (CliErrorException e)
            {
                Log.Warning(e, "CLI parse error in ExtractCommand");
                return Fail(e.Message);
            }

            if (!options.IsSet("input"))
            {
                return Fail("No input set");
            }

            if (!options.IsSet("output") && !options.HasFlag("list"))
            {
                return Fail("No output folder set");
            }

            var inputPath = options.Get("input")!;
            var outputPath = options.Get("output") ?? string.Empty;
            var quiet = options.HasFlag("quiet");
            var listOnly = options.HasFlag("list");
            var singlePath = options.Get("single") ?? string.Empty;
            var specificPartition = options.Get("partition") ?? string.Empty;
            if (options.HasFlag("gameonly"))
            {
                specificPartition = "DATA";
            }

            using var spooledInput = inputPath == "-" ? TemporaryFile.SpoolStdin() : null;
            var path = spooledInput?.Path ?? inputPath;

            IBlobReader blob;
            try
            {
                var file = File.OpenRead(path);
                blob = Blob.Open(file, filePath: path, leaveOpen: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RvzException)
            {
                Log.Error(e, "Failed to open input file '{InputPath}'", path);
                return Fail("The input file could not be opened.");
            }

            using (blob)
            {
                if (!Blob.IsDisc(blob))
                {
                    return Fail("The input is not a GameCube or Wii disc image.");
                }

                var partitions = WiiVolume.GetPartitions(blob);
                return listOnly
                    ? ExtractList(blob, partitions, singlePath, specificPartition, outputPath)
                    : ExtractToFolder(blob, partitions, singlePath, specificPartition, outputPath,
                        quiet);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Extract command failed");
            return Fail(e.Message);
        }
    }

    private static int ExtractList(IBlobReader blob, IReadOnlyList<Partition> partitions,
        string singlePath, string specificPartition, string outputPath)
    {
        var listPath = singlePath.Length > 0 ? singlePath : "/";
        var text = new StringBuilder();
        var found = false;

        if (partitions.Count == 0)
        {
            found = ListPartition(blob, null, string.Empty, listPath, text);
        }
        else
        {
            foreach (var partition in partitions)
            {
                var name = PartitionName(partition.Type);
                if (specificPartition.Length > 0 &&
                    !string.Equals(name, specificPartition, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                text.Append($"/// PARTITION: {name} <{listPath}> ///\n");
                found |= ListPartition(blob, partition, name, listPath, text);
            }
        }

        if (!found)
        {
            return Fail("Found nothing to list");
        }

        if (outputPath.Length > 0)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputPath, text.ToString());
        }

        return 0;
    }

    private static bool ListPartition(IBlobReader blob, Partition? partition, string partitionName,
        string path, StringBuilder text)
    {
        using var fs = TryOpenFileSystem(blob, partition, partitionName);
        if (fs is null)
        {
            return false;
        }

        var info = fs.Find(path);
        if (info is null)
        {
            if (partitionName.Length > 0)
            {
                Log.Warning("{Path} does not exist in partition {Partition}", path, partitionName);
            }

            return false;
        }

        ListRecursively(info, text);
        return true;
    }

    private static void ListRecursively(DiscFileInfo info, StringBuilder text)
    {
        if (!info.IsRoot)
        {
            var line = info.Path + "\n";
            Console.Write(line);
            text.Append(line);
        }

        foreach (var child in info.Children)
        {
            ListRecursively(child, text);
        }
    }

    private static int ExtractToFolder(IBlobReader blob, IReadOnlyList<Partition> partitions,
        string singlePath, string specificPartition, string outputFolder, bool quiet)
    {
        var extracted = false;

        if (partitions.Count == 0)
        {
            if (specificPartition.Length > 0)
            {
                Log.Warning(
                    "--partition has a value even though this image doesn't have any partitions.");
            }

            extracted = ExtractPartition(blob, null, string.Empty, singlePath, outputFolder, quiet);
        }
        else
        {
            foreach (var partition in partitions)
            {
                var name = PartitionName(partition.Type);
                if (specificPartition.Length > 0 &&
                    !string.Equals(name, specificPartition, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                extracted |= ExtractPartition(blob, partition, name, singlePath, outputFolder, quiet);
            }
        }

        if (!extracted)
        {
            if (singlePath.Length > 0)
            {
                return Fail("No file/folder was extracted.");
            }

            return Fail(specificPartition.Length > 0
                ? "No partitions were extracted. Maybe you misspelled your specified partition?"
                : "No partitions were extracted.");
        }

        if (!quiet)
        {
            Console.Error.WriteLine("Finished Successfully!");
        }

        return 0;
    }

    private static bool ExtractPartition(IBlobReader blob, Partition? partition,
        string partitionName, string singlePath, string outputFolder, bool quiet)
    {
        var basePath = partitionName.Length > 0
            ? Path.Combine(outputFolder, partitionName)
            : outputFolder;
        using var fs = TryOpenFileSystem(blob, partition, partitionName);

        if (singlePath.Length > 0)
        {
            if (fs is null)
            {
                return false;
            }

            var info = fs.Find(singlePath);
            if (info is null)
            {
                return false;
            }

            var target = Path.Combine(basePath, "files",
                singlePath.Replace('/', Path.DirectorySeparatorChar));
            if (info.IsDirectory)
            {
                ExtractDirectory(fs, info, target, quiet);
            }
            else
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(target));
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                using var output = File.Create(target);
                fs.CopyFileTo(info, output);
                if (!quiet)
                {
                    Console.Error.WriteLine($"Extracting: {info.Path}");
                }
            }

            return true;
        }

        if (fs is not null)
        {
            ExtractDirectory(fs, fs.Root, Path.Combine(basePath, "files"), quiet);
        }

        ExportSystemData(blob, fs, partition, basePath);
        return true;
    }

    private static void ExtractDirectory(DiscFileSystem fs, DiscFileInfo directory,
        string exportFolder, bool quiet)
    {
        Directory.CreateDirectory(exportFolder);
        foreach (var child in directory.Children)
        {
            var path = Path.Combine(exportFolder,
                child.Name + (child.IsDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty));
            if (child.IsDirectory)
            {
                ExtractDirectory(fs, child, path, quiet);
            }
            else
            {
                using var output = File.Create(path);
                fs.CopyFileTo(child, output);
                if (!quiet)
                {
                    Console.Error.WriteLine($"Extracting: {child.Path}");
                }
            }
        }
    }

    private static DiscFileSystem? TryOpenFileSystem(IBlobReader blob, Partition? partition,
        string partitionName)
    {
        try
        {
            return partition is null
                ? DiscFileSystem.Open(blob)
                : DiscFileSystem.Open(blob, partition.Value);
        }
        catch (RvzException e)
        {
            Log.Warning("Partition '{Partition}' has no usable file system ({Message})",
                partitionName, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Exports the standard system data files (Dolphin: ExportSystemData): the decrypted boot
    /// header, BI2, apploader, DOL and FST, plus the Wii disc header/region data and the
    /// partition ticket, TMD, certificate chain and H3 table.
    /// </summary>
    private static void ExportSystemData(IBlobReader blob, DiscFileSystem? fs,
        Partition? partition, string basePath)
    {
        IBlobReader view = blob;
        PartitionReader? partitionView = null;
        if (partition is not null)
        {
            partitionView = new PartitionReader(blob, partition.Value);
            view = partitionView;
        }

        try
        {
            var sys = Path.Combine(basePath, "sys");
            Directory.CreateDirectory(sys);
            CopyData(view, 0x0, 0x440, Path.Combine(sys, "boot.bin"));
            CopyData(view, 0x440, 0x2000, Path.Combine(sys, "bi2.bin"));
            if (WiiVolume.GetApploaderSize(view) is { } apploaderSize)
            {
                CopyData(view, 0x2440, (long)apploaderSize, Path.Combine(sys, "apploader.img"));
            }

            if (WiiVolume.GetBootDolOffset(view) is { } dolOffset &&
                WiiVolume.GetBootDolSize(view, dolOffset) is { } dolSize)
            {
                CopyData(view, (long)dolOffset, dolSize, Path.Combine(sys, "main.dol"));
            }

            if (fs is not null)
            {
                CopyData(view, (long)fs.FstOffset, (long)fs.FstSize, Path.Combine(sys, "fst.bin"));
            }

            if (partition is null)
            {
                return;
            }

            var disc = Path.Combine(basePath, "disc");
            Directory.CreateDirectory(disc);
            CopyData(blob, 0x0, 0x100, Path.Combine(disc, "header.bin"));
            CopyData(blob, 0x4E000, 0x20, Path.Combine(disc, "region.bin"));

            var offset = (long)partition.Value.Offset;
            CopyData(blob, offset, 0x2A4, Path.Combine(basePath, "ticket.bin"));
            CopyPartitionBlob(blob, offset + 0x2A4, offset + 0x2A8,
                Path.Combine(basePath, "tmd.bin"));
            CopyPartitionBlob(blob, offset + 0x2AC, offset + 0x2B0,
                Path.Combine(basePath, "cert.bin"));
            CopyPartitionBlob(blob, offset + 0x2B4, null, Path.Combine(basePath, "h3.bin"),
                fixedSize: 0x18000);
        }
        finally
        {
            partitionView?.Dispose();
        }
    }

    /// <summary>Reads a size/shifted-offset pair from the partition header and copies the blob.</summary>
    private static void CopyPartitionBlob(IBlobReader blob, long sizeAddress, long? offsetAddress,
        string path, int fixedSize = 0)
    {
        var size = fixedSize;
        if (size == 0 && !TryReadBe32(blob, sizeAddress, out size))
        {
            return;
        }

        if (offsetAddress is { } offsetAddressValue)
        {
            if (!TryReadBe32(blob, offsetAddressValue, out var offset))
            {
                return;
            }

            CopyData(blob, (long)((ulong)offset << 2), size, path);
        }
        else if (TryReadBe32(blob, sizeAddress, out var rawOffset))
        {
            CopyData(blob, (long)((ulong)rawOffset << 2), size, path);
        }
    }

    private static bool TryReadBe32(IBlobReader blob, long offset, out int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (blob.ReadAt(offset, bytes) == 4)
        {
            value = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
            return true;
        }

        value = 0;
        return false;
    }

    private static void CopyData(IBlobReader reader, long offset, long size, string path)
    {
        using var output = File.Create(path);
        var buffer = new byte[1 << 20];
        var position = offset;
        var remaining = size;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = reader.ReadAt(position, buffer.AsSpan(0, take));
            if (read <= 0)
            {
                throw new RvzFormatException(
                    $"Data ended at 0x{position:X} while exporting '{path}'.");
            }

            output.Write(buffer, 0, read);
            position += read;
            remaining -= read;
        }
    }

    /// <summary>Partition folder name (Dolphin: NameForPartitionType with the P- prefix).</summary>
    private static string PartitionName(uint type)
    {
        switch (type)
        {
            case 0:
                return "DATA";
            case 1:
                return "UPDATE";
            case 2:
                return "CHANNEL";
        }

        var id = new string(new[]
        {
            (char)((type >> 24) & 0xFF),
            (char)((type >> 16) & 0xFF),
            (char)((type >> 8) & 0xFF),
            (char)(type & 0xFF)
        });
        if (id.All(char.IsAsciiLetterOrDigit))
        {
            return "P-" + id;
        }

        return "P" + type;
    }

    // ------------------------------------------------------------------
    // Legacy commands: info / decode.
    // ------------------------------------------------------------------

    private static int Info(string path)
    {
        try
        {
            using var spooledInput = path == "-" ? TemporaryFile.SpoolStdin() : null;
            var readPath = spooledInput?.Path ?? path;

            using var file = File.OpenRead(readPath);
            using var reader = Blob.Open(file, filePath: readPath, leaveOpen: true);

            Console.WriteLine($"file:            {path}");
            Console.WriteLine($"format:          {Blob.GetName(reader.Type)}");
            Console.WriteLine($"iso size:        {reader.Length} bytes (0x{reader.Length:X})");

            switch (reader)
            {
                case RvzReader rvz:
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

                    break;
                }
                case GczBlob gcz:
                    Console.WriteLine($"block size:      0x{gcz.BlockSize:X}");
                    Console.WriteLine($"blocks:          {gcz.NumBlocks}");
                    Console.WriteLine("compression:     Deflate");
                    break;
                default:
                {
                    if (reader.BlockSize != 0)
                    {
                        Console.WriteLine($"block size:      0x{reader.BlockSize:X}");
                    }

                    break;
                }
            }

            return 0;
        }
        catch (Exception e)
        {
            Log.Error(e, "Info command failed for path '{Path}'", path);
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static int Decode(string inputPath, string outputPath, IReadOnlyList<string> args)
    {
        try
        {
            string? expectedSha1 = null;
            var maxThreads = 1;
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "--sha1")
                {
                    expectedSha1 = args[i + 1];
                }
                else if (args[i] == "--threads" &&
                         (!int.TryParse(args[i + 1], out maxThreads) || maxThreads < 0))
                {
                    return Fail("Threads must be a non-negative integer (0 = processor count)");
                }
            }

            using var spooledInput = inputPath == "-" ? TemporaryFile.SpoolStdin() : null;
            using var spooledOutput = outputPath == "-" ? TemporaryFile.Create() : null;
            var readPath = spooledInput?.Path ?? inputPath;
            var writePath = spooledOutput?.Path ?? outputPath;

            using var input = File.OpenRead(readPath);
            using var reader = Blob.Open(input, filePath: readPath, leaveOpen: true);
            var result = DecodeBlob(reader, writePath, expectedSha1, maxThreads, outputPath,
                quiet: spooledOutput != null);
            if (result == 0 && spooledOutput != null)
            {
                spooledOutput.CopyToStdout();
            }

            return result;
        }
        catch (Exception e)
        {
            Log.Error(e, "Decode command failed");
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static int DecodeBlob(IBlobReader reader, string outputPath, string? expectedSha1,
        int maxThreads = 1, string? displayPath = null, bool quiet = false)
    {
        try
        {
            using var output = File.Create(outputPath);
            using var hashing = new HashingStream(output);
            var progress = new ConsoleProgress("Decoding ");
            try
            {
                reader.CopyTo(hashing, progress, maxThreads, Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                ConsoleProgress.Clear();
                Console.Error.WriteLine("Canceled.");
                return 130;
            }

            ConsoleProgress.Clear();

            if (expectedSha1 != null)
            {
                var actual = Convert.ToHexString(hashing.GetHashAndReset()).ToLowerInvariant();
                if (!quiet)
                {
                    Console.WriteLine($"sha1: {actual}");
                }

                if (!string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    Log.Warning("SHA-1 mismatch: expected {Expected}, got {Actual}", expectedSha1, actual);
                    Console.Error.WriteLine($"error: SHA-1 mismatch (expected {expectedSha1}).");
                    return 1;
                }
            }

            if (!quiet)
            {
                Console.WriteLine($"decoded {reader.Length} bytes to {displayPath ?? outputPath}");
            }

            return 0;
        }
        catch (Exception e)
        {
            ConsoleProgress.Clear();
            Log.Error(e, "DecodeBlob failed");
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }
}
