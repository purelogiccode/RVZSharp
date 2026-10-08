using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Encodes any decoded disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>)
/// into the CISO / WBI format (Dolphin: CISOBlob, wit: wbi): a 0x8000-byte header holding a
/// one-byte-per-block presence map, then only the blocks that contain data. All-zero blocks are
/// stored absent and decode back to zeroes, so scrubbed images shrink dramatically. The decoded
/// image is always <c>BlockSize × 0x7FF8</c> bytes (the map capacity).
/// </summary>
public static class CisoWriter
{
    /// <summary>
    /// Writes <paramref name="input"/> as a CISO file to <paramref name="output"/>. The output
    /// stream must be seekable: the presence map is written after the block data.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="CisoWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between blocks.</param>
    /// <exception cref="ArgumentException">
    /// Invalid block size, a non-seekable output stream, or an input too small for the format.
    /// </exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image, or the image does not fit in the CISO
    /// block map (use a larger block size).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output, CisoWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= CisoWriteOptions.Default;

        var blockSize = options.BlockSize;
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentException("Block size must be a power of two.", nameof(options));
        }

        if (!output.CanSeek)
        {
            throw new ArgumentException(
                "The CISO output stream must be seekable: the block map is written after "
                + "the block data.",
                nameof(output));
        }

        var discSize = (ulong)input.Length;
        if (discSize < 0x80)
        {
            throw new ArgumentException(
                $"Input is too small to be a disc image ({discSize} bytes).", nameof(input));
        }

        if (WiiVolume.GetDiscType(input) == DiscType.Unknown)
        {
            throw new RvzFormatException(
                "The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).");
        }

        // Optional scrubbing: zero the non-game Wii partitions (Dolphin: DiscScrubber). The
        // wrapper is a no-op for GameCube discs and Wii images without a game partition.
        if (options.Scrub)
        {
            input = ScrubbedBlob.Create(input) ?? input;
        }

        var numBlocks = (discSize + (ulong)blockSize - 1) / (ulong)blockSize;
        if (numBlocks > CisoBlob.MapSize)
        {
            throw new RvzFormatException(
                $"The image needs {numBlocks} blocks of {blockSize} bytes, but the CISO map "
                + $"holds at most {CisoBlob.MapSize} (use a larger block size).");
        }

        var start = output.Position;

        // Header: magic + little-endian block size + a zeroed presence map (filled in later).
        Span<byte> header = stackalloc byte[8];
        "CISO"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)blockSize);
        output.Write(header);
        output.Write(new byte[CisoBlob.MapSize]);

        var map = new byte[CisoBlob.MapSize];
        var block = new byte[blockSize];
        var blockCount = (int)numBlocks;
        for (var i = 0; i < blockCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = (long)((ulong)i * (ulong)blockSize);
            var take = (int)Math.Min((ulong)blockSize, discSize - (ulong)offset);
            Array.Clear(block);
            if (input.ReadAt(offset, block.AsSpan(0, take)) != take)
            {
                throw new RvzFormatException($"The input ended early at offset {offset}.");
            }

            // All-zero blocks are stored absent (decode to zeroes), like wit's scrubber.
            if (block.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                map[i] = 1;
                output.Write(block);
            }

            progress?.Report((double)(i + 1) / blockCount);
        }

        var end = output.Position;
        output.Position = start + 8;
        output.Write(map);
        output.Position = end;
        output.Flush();
    }

    /// <summary>
    /// Asynchronous form of <see cref="Write(IBlobReader, Stream, CisoWriteOptions, IProgress{double}, CancellationToken)"/>.
    /// The encoder is synchronous and I/O-bound, so the work runs on the thread pool; the
    /// returned task completes when the file has been written.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="CisoWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between blocks.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(IBlobReader input, Stream output, CisoWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(input, output, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Writes the disc image at <paramref name="inputPath"/> (any supported format,
    /// auto-detected by <see cref="Blob.Open(string)"/>) as a CISO file at
    /// <paramref name="outputPath"/>. The output file is created or truncated; a partial
    /// file can remain when the operation fails.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube or Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the CISO file to create.</param>
    /// <param name="options">Writer options; <see cref="CisoWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between blocks.</param>
    /// <exception cref="IOException">The input cannot be opened or the output cannot be written.</exception>
    /// <exception cref="ArgumentException">Invalid block size or an input too small for the format.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image, or the image does not fit in the map.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(string inputPath, string outputPath, CisoWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var input = Blob.Open(inputPath);
        using var output = File.Create(outputPath);
        Write(input, output, options, progress, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of
    /// <see cref="Write(string, string, CisoWriteOptions?, IProgress{double}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube or Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the CISO file to create.</param>
    /// <param name="options">Writer options; <see cref="CisoWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between blocks.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(string inputPath, string outputPath,
        CisoWriteOptions? options = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(inputPath, outputPath, options, progress, cancellationToken),
            cancellationToken);
    }
}
