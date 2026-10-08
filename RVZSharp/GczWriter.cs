using System.Buffers.Binary;
using ICSharpCode.SharpZipLib.Zip.Compression;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.IO;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Encodes any decoded disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>)
/// into the GCZ format, mirroring Dolphin's ConvertToGCZ: the image is split into blocks
/// (16 KiB by default), each block is deflated at level 9 and stored compressed unless that
/// saves fewer than 10 bytes, and the block table carries the top bit for stored-raw blocks
/// plus a per-block Adler-32 of the stored bytes.
/// </summary>
public static class GczWriter
{
    private const int HeaderSize = 32;
    private const uint Magic = 0xB10BC001;
    private const ulong UncompressedFlag = 1UL << 63;

    /// <summary>
    /// Writes <paramref name="input"/> as a GCZ file to <paramref name="output"/>. The output
    /// stream must be seekable: the header and the block tables are written after the block
    /// data, exactly like Dolphin's converter.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="GczWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between block batches.</param>
    /// <exception cref="ArgumentException">
    /// Invalid block size, negative <see cref="GczWriteOptions.MaxThreads"/>, a non-seekable
    /// output stream, or an input too small for the format.
    /// </exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output, GczWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= GczWriteOptions.Default;

        var blockSize = options.BlockSize;
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentException("Block size must be a power of two.", nameof(options));
        }

        if (options.MaxThreads < 0)
        {
            throw new ArgumentException(
                "MaxThreads must be zero (use the processor count) or a positive number.",
                nameof(options));
        }

        if (!output.CanSeek)
        {
            throw new ArgumentException(
                "The GCZ output stream must be seekable: the header and block tables are "
                + "written after the block data.",
                nameof(output));
        }

        var discSize = (ulong)input.Length;
        if (discSize < 0x80)
        {
            throw new ArgumentException(
                $"Input is too small to be a disc image ({discSize} bytes).", nameof(input));
        }

        var discType = WiiVolume.GetDiscType(input);
        if (discType == DiscType.Unknown)
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

        // Round upwards like Dolphin: the last block is zero-padded to the block size.
        var numBlocks = (discSize + (ulong)blockSize - 1) / (ulong)blockSize;
        var tableSize = (long)numBlocks * 12; // u64 block pointers + u32 Adler-32 hashes
        if (numBlocks > int.MaxValue || tableSize > int.MaxValue - HeaderSize)
        {
            throw new ArgumentException(
                "The input has too many blocks for the GCZ format (increase the block size).",
                nameof(options));
        }

        // Seek past the header and the tables (written at the end, Dolphin: ConvertToGCZ).
        var start = output.Position;
        var dataStart = start + HeaderSize + tableSize;
        output.Position = dataStart;

        var blockCount = (int)numBlocks;
        var offsets = new ulong[blockCount];
        var hashes = new uint[blockCount];
        var maxThreads = options.MaxThreads > 0 ? options.MaxThreads : Environment.ProcessorCount;
        var batchBlocks = (int)Math.Min(blockCount, Math.Max(1L, (long)maxThreads * 4));
        var blocks = new byte[batchBlocks][];
        var results = new BlockResult[batchBlocks];
        var position = dataStart;

        for (var firstBlock = 0; firstBlock < blockCount; firstBlock += batchBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batchBlocks, blockCount - firstBlock);

            for (var i = 0; i < count; i++)
            {
                var blockOffset = (ulong)(firstBlock + i) * (ulong)blockSize;
                var toRead = (int)Math.Min((ulong)blockSize, discSize - blockOffset);
                // The tail of the last block stays zero, like Dolphin's in_buf fill.
                var block = new byte[blockSize];
                if (input.ReadAt((long)blockOffset, block.AsSpan(0, toRead)) != toRead)
                {
                    throw new RvzFormatException($"The input ended early at offset {blockOffset}.");
                }

                blocks[i] = block;
            }

            if (count == 1 || maxThreads == 1)
            {
                var compressor = new BlockCompressor(blockSize);
                for (var i = 0; i < count; i++)
                {
                    results[i] = compressor.Compress(blocks[i]);
                }
            }
            else
            {
                Parallel.For(0, count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxThreads,
                        CancellationToken = cancellationToken
                    },
                    () => new BlockCompressor(blockSize),
                    (i, _, compressor) =>
                    {
                        results[i] = compressor.Compress(blocks[i]);
                        return compressor;
                    },
                    _ => { });
            }

            for (var i = 0; i < count; i++)
            {
                var blockIndex = firstBlock + i;
                var result = results[i];
                var offset = (ulong)(position - dataStart);
                offsets[blockIndex] = result.Compressed ? offset : offset | UncompressedFlag;
                hashes[blockIndex] = Adler32.Compute(result.Stored);
                output.Write(result.Stored);
                position += result.Stored.Length;
                blocks[i] = null!;
                results[i] = default;
            }

            progress?.Report(Math.Min(1.0, (double)(firstBlock + count) / blockCount));
        }

        var compressedDataSize = (ulong)(position - dataStart);
        var subType = discType == DiscType.Wii ? 1u : 0u;
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], subType);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], compressedDataSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], discSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)blockCount);

        var table = new byte[(long)blockCount * 8];
        for (var i = 0; i < blockCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(i * 8), offsets[i]);
        }

        output.Position = start;
        output.Write(header);
        output.Write(table);
        for (var i = 0; i < blockCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), hashes[i]);
        }

        output.Write(table, 0, blockCount * 4);
        output.Position = dataStart + (long)compressedDataSize;
        output.Flush();
    }

    /// <summary>
    /// Asynchronous form of <see cref="Write(IBlobReader, Stream, GczWriteOptions, IProgress{double}, CancellationToken)"/>. Compression is synchronous and CPU-bound, so
    /// the work runs on the thread pool; the returned task completes when the file has been
    /// written.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="GczWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between block batches.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    /// <exception cref="ArgumentException">
    /// Invalid block size, negative <see cref="GczWriteOptions.MaxThreads"/>, a non-seekable
    /// output stream, or an input too small for the format.
    /// </exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static Task WriteAsync(IBlobReader input, Stream output, GczWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(input, output, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Writes the disc image at <paramref name="inputPath"/> (any supported format,
    /// auto-detected by <see cref="Blob.Open(string)"/>) as a GCZ file at
    /// <paramref name="outputPath"/>. The output file is created or truncated; a partial
    /// file can remain when the operation fails.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube or Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the GCZ file to create.</param>
    /// <param name="options">Writer options; <see cref="GczWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between block batches.</param>
    /// <exception cref="IOException">The input cannot be opened or the output cannot be written.</exception>
    /// <exception cref="ArgumentException">Invalid block size or an input too small for the format.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(string inputPath, string outputPath, GczWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var input = Blob.Open(inputPath);
        using var output = File.Create(outputPath);
        Write(input, output, options, progress, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of
    /// <see cref="Write(string, string, GczWriteOptions?, IProgress{double}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube or Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the GCZ file to create.</param>
    /// <param name="options">Writer options; <see cref="GczWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the blocks processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between block batches.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(string inputPath, string outputPath, GczWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(inputPath, outputPath, options, progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>The outcome of compressing one block: the stored bytes and whether they are deflated.</summary>
    private readonly record struct BlockResult(byte[] Stored, bool Compressed);

    /// <summary>
    /// One deflate state per worker thread (Dolphin: CompressThreadState with deflateReset).
    /// Blocks are compressed at level 9; a block is stored raw when the deflate stream does not
    /// finish inside the block-size buffer or saves fewer than 10 bytes.
    /// </summary>
    private sealed class BlockCompressor
    {
        private readonly Deflater _deflater = new(Deflater.BEST_COMPRESSION);
        private readonly byte[] _output;
        private readonly int _blockSize;

        public BlockCompressor(int blockSize)
        {
            _blockSize = blockSize;
            _output = new byte[blockSize];
        }

        public BlockResult Compress(byte[] block)
        {
            _deflater.Reset();
            _deflater.SetInput(block, 0, _blockSize);
            _deflater.Finish();
            var total = 0;
            while (!_deflater.IsFinished && total < _output.Length)
            {
                var written = _deflater.Deflate(_output, total, _output.Length - total);
                if (written == 0)
                {
                    break; // the compressed block does not fit in the block-size buffer
                }

                total += written;
            }

            if (_deflater.IsFinished && total <= _blockSize - 10)
            {
                var stored = new byte[total];
                _output.AsSpan(0, total).CopyTo(stored);
                return new BlockResult(stored, true);
            }

            return new BlockResult(block, false);
        }
    }
}
