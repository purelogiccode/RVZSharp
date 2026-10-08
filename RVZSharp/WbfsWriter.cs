using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Encodes a Wii disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>) into a
/// standalone WBFS file (Dolphin: WbfsFileReader, wit: wbfs): a 512-byte header with the disc
/// table, the u16 big-endian disc-block → volume-cluster map, and the volume clusters. All-zero
/// disc clusters share one zero-filled volume cluster, so scrubbed images shrink dramatically.
/// The decoded image is always the fixed Wii double-layer size (an upper bound).
/// </summary>
public static class WbfsWriter
{
    private const int HdSectorSize = 512;
    private const int DiscHeaderSize = 256;
    private const int DiscTableOffset = HdSectorSize + DiscHeaderSize;

    /// <summary>
    /// Writes <paramref name="input"/> as a WBFS file to <paramref name="output"/>. The output
    /// stream must be seekable: the disc table is written after the volume data.
    /// </summary>
    /// <param name="input">A Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="WbfsWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the disc clusters processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between clusters.</param>
    /// <exception cref="ArgumentException">
    /// Invalid cluster size, a non-seekable output stream, or an input too small for the format.
    /// </exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a Wii disc image, or its block map does not fit in the format (use a
    /// larger cluster size).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output, WbfsWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= WbfsWriteOptions.Default;

        var clusterSize = options.BlockSize;
        if (clusterSize < 0x8000 || (clusterSize & (clusterSize - 1)) != 0)
        {
            throw new ArgumentException(
                "Cluster size must be a power of two of at least 32 KiB.", nameof(options));
        }

        if (!output.CanSeek)
        {
            throw new ArgumentException(
                "The WBFS output stream must be seekable: the disc table is written after "
                + "the volume data.",
                nameof(output));
        }

        if (input.Length < 0x80)
        {
            throw new ArgumentException(
                $"Input is too small to be a disc image ({input.Length} bytes).", nameof(input));
        }

        if (WiiVolume.GetDiscType(input) != DiscType.Wii)
        {
            throw new RvzFormatException("The WBFS format only supports Wii disc images.");
        }

        // Optional scrubbing: zero the non-game Wii partitions (Dolphin: DiscScrubber). The
        // wrapper is a no-op for Wii images without a game partition.
        if (options.Scrub)
        {
            input = ScrubbedBlob.Create(input) ?? input;
        }

        // The u16 disc-block map caps the disc at 65535 clusters (Dolphin: WbfsBlob).
        var discSize = WbfsBlob.WiiDataSize;
        var blocksPerDisc = (discSize + clusterSize - 1) / clusterSize;
        if (blocksPerDisc > ushort.MaxValue)
        {
            throw new RvzFormatException(
                $"The disc needs {blocksPerDisc} clusters of {clusterSize} bytes, but the WBFS "
                + $"map holds at most {ushort.MaxValue} (use a larger cluster size).");
        }

        var tableSize = blocksPerDisc * 2L;
        var dataClusterBase = (int)((DiscTableOffset + tableSize + clusterSize - 1) / clusterSize);
        var dataStart = (long)dataClusterBase * clusterSize;

        var start = output.Position;
        if (start % HdSectorSize != 0)
        {
            throw new ArgumentException(
                "The WBFS output stream position must be a multiple of 512.", nameof(output));
        }

        // Header (magic, sector count/shift, cluster shift, disc table) and the map are filled
        // in after the volume data, when every cluster index is known.
        output.Write(new byte[DiscTableOffset + tableSize]);
        output.Position = start + dataStart;

        var wlba = new ushort[blocksPerDisc];
        var zeroCluster = -1;
        var volumeCluster = dataClusterBase;
        var cluster = new byte[clusterSize];
        var blockCount = (int)blocksPerDisc;
        // The decoded image is the fixed Wii size; clusters past the input are all zero and
        // share the zero-filled volume cluster like any other all-zero cluster.
        var inputSize = Math.Min((ulong)input.Length, (ulong)discSize);
        for (var i = 0; i < blockCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = (long)i * clusterSize;
            var take = offset >= (long)inputSize
                ? 0
                : (int)Math.Min((ulong)clusterSize, inputSize - (ulong)offset);
            Array.Clear(cluster);
            if (take > 0 && input.ReadAt(offset, cluster.AsSpan(0, take)) != take)
            {
                throw new RvzFormatException($"The input ended early at offset {offset}.");
            }

            if (cluster.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                if (volumeCluster > ushort.MaxValue)
                {
                    throw new RvzFormatException(
                        "The WBFS disc table overflowed (use a larger cluster size).");
                }

                wlba[i] = (ushort)volumeCluster++;
                output.Write(cluster);
            }

            progress?.Report((double)(i + 1) / blockCount);
        }

        // Every all-zero disc cluster maps to one shared zero-filled volume cluster
        // (Dolphin's scrubber does the same); the reader rejects unmapped clusters.
        zeroCluster = volumeCluster;
        if (zeroCluster > ushort.MaxValue)
        {
            throw new RvzFormatException(
                "The WBFS disc table overflowed (use a larger cluster size).");
        }

        output.Write(new byte[clusterSize]);
        for (var i = 0; i < blockCount; i++)
        {
            if (wlba[i] == 0)
            {
                wlba[i] = (ushort)zeroCluster;
            }
        }

        var end = output.Position;
        var hdSectorCount = (end - start) / HdSectorSize;
        if (hdSectorCount > uint.MaxValue)
        {
            throw new RvzFormatException("The WBFS file is too large for the format.");
        }

        Span<byte> header = stackalloc byte[DiscTableOffset];
        header.Clear();
        "WBFS"u8.CopyTo(header);
        header[4] = (byte)(hdSectorCount >> 24);
        header[5] = (byte)(hdSectorCount >> 16);
        header[6] = (byte)(hdSectorCount >> 8);
        header[7] = (byte)hdSectorCount;
        header[8] = 9; // hd_sector_shift: 512-byte sectors
        header[9] = (byte)System.Numerics.BitOperations.Log2((uint)clusterSize);
        header[12] = 1; // disc_table[0]: a disc is present in slot 0
        // The 256 bytes after the header hold a copy of the disc header so tools can identify
        // the disc without reading the volume data (Dolphin skips this region: WbfsBlob.cpp).
        input.ReadAt(0, header.Slice(HdSectorSize, DiscHeaderSize));

        output.Position = start;
        output.Write(header);
        for (var i = 0; i < blockCount; i++)
        {
            output.WriteByte((byte)(wlba[i] >> 8));
            output.WriteByte((byte)wlba[i]);
        }

        output.Position = end;
        output.Flush();
    }

    /// <summary>
    /// Asynchronous form of <see cref="Write(IBlobReader, Stream, WbfsWriteOptions, IProgress{double}, CancellationToken)"/>.
    /// The encoder is synchronous and I/O-bound, so the work runs on the thread pool; the
    /// returned task completes when the file has been written.
    /// </summary>
    /// <param name="input">A Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method; must be seekable).</param>
    /// <param name="options">Writer options; <see cref="WbfsWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the disc clusters processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between clusters.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(IBlobReader input, Stream output, WbfsWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(input, output, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Writes the disc image at <paramref name="inputPath"/> (any supported format,
    /// auto-detected by <see cref="Blob.Open(string)"/>) as a WBFS file at
    /// <paramref name="outputPath"/>. The output file is created or truncated; a partial
    /// file can remain when the operation fails.
    /// </summary>
    /// <param name="inputPath">Path of a Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the WBFS file to create.</param>
    /// <param name="options">Writer options; <see cref="WbfsWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the disc clusters processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between clusters.</param>
    /// <exception cref="IOException">The input cannot be opened or the output cannot be written.</exception>
    /// <exception cref="ArgumentException">Invalid cluster size or an input too small for the format.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a Wii disc image, or its block map does not fit in the format.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(string inputPath, string outputPath, WbfsWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var input = Blob.Open(inputPath);
        using var output = File.Create(outputPath);
        Write(input, output, options, progress, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of
    /// <see cref="Write(string, string, WbfsWriteOptions?, IProgress{double}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="inputPath">Path of a Wii disc image in any supported format.</param>
    /// <param name="outputPath">Path of the WBFS file to create.</param>
    /// <param name="options">Writer options; <see cref="WbfsWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the disc clusters processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between clusters.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(string inputPath, string outputPath,
        WbfsWriteOptions? options = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(inputPath, outputPath, options, progress, cancellationToken),
            cancellationToken);
    }
}
