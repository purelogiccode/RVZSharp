using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Encodes a GameCube disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>)
/// into the TGC format (Dolphin: TGCBlob): a 56-byte header carrying the DOL/FST locations,
/// followed by the ISO bytes. The DOL/FST offsets in the header are stored ISO-relative plus
/// the header size, so the decoded image is byte-identical to the input.
/// </summary>
public static class TgcWriter
{
    /// <summary>Size in bytes of the TGC header structure (0x38).</summary>
    public const int HeaderSize = 0x38;

    private const uint MagicValue = 0xA2380FAE;

    /// <summary>
    /// Writes <paramref name="input"/> as a TGC file to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">A GameCube disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method).</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes copied.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <exception cref="ArgumentException">The input is too small to be a disc image.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube disc image (the TGC format cannot hold Wii discs).
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (input.Length < 0x80)
        {
            throw new ArgumentException(
                $"Input is too small to be a disc image ({input.Length} bytes).", nameof(input));
        }

        if (WiiVolume.GetDiscType(input) != DiscType.GameCube)
        {
            throw new RvzFormatException("The TGC format only supports GameCube disc images.");
        }

        // The disc header stores the DOL/FST locations; the TGC header repeats them shifted by
        // the header size. The file area starts right after the FST (wit's convention), and
        // storing file_area_real - file_area_virtual == header_size makes the reader's FST
        // relocation a no-op, so the decoded image is the input bytes verbatim.
        Span<byte> discHeader = stackalloc byte[0x430];
        if (!TryReadExactly(input, 0, discHeader))
        {
            throw new RvzFormatException("The disc header could not be read.");
        }

        var dolOffset = ReadBe32(discHeader, 0x420);
        var fstOffset = ReadBe32(discHeader, 0x424);
        var fstSize = ReadBe32(discHeader, 0x428);
        var fstMaxSize = ReadBe32(discHeader, 0x42C);
        var fileAreaVirtual = unchecked(fstOffset + fstSize);
        var dolSize = GetDolSize(input, dolOffset);

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicValue); // the one LE field
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], 0x80); // disc header area size
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], unchecked(fstOffset + HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], fstSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], fstMaxSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[28..], unchecked(dolOffset + HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(header[32..], dolSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[36..], unchecked(fileAreaVirtual + HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(header[52..], fileAreaVirtual);
        output.Write(header);

        IO.BlobCopy.CopyTo(input, output, progress, cancellationToken);
        output.Flush();
    }

    /// <summary>
    /// Asynchronous form of <see cref="Write(IBlobReader, Stream, IProgress{double}, CancellationToken)"/>.
    /// The encoder is synchronous and I/O-bound, so the work runs on the thread pool; the
    /// returned task completes when the file has been written.
    /// </summary>
    /// <param name="input">A GameCube disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method).</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes copied.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(IBlobReader input, Stream output,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Write(input, output, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Writes the disc image at <paramref name="inputPath"/> (any supported format,
    /// auto-detected by <see cref="Blob.Open(string)"/>) as a TGC file at
    /// <paramref name="outputPath"/>. The output file is created or truncated; a partial
    /// file can remain when the operation fails.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube disc image in any supported format.</param>
    /// <param name="outputPath">Path of the TGC file to create.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes copied.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <exception cref="IOException">The input cannot be opened or the output cannot be written.</exception>
    /// <exception cref="ArgumentException">The input is too small to be a disc image.</exception>
    /// <exception cref="RvzFormatException">The input is not a GameCube disc image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(string inputPath, string outputPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var input = Blob.Open(inputPath);
        using var output = File.Create(outputPath);
        Write(input, output, progress, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of
    /// <see cref="Write(string, string, IProgress{double}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="inputPath">Path of a GameCube disc image in any supported format.</param>
    /// <param name="outputPath">Path of the TGC file to create.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes copied.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    public static Task WriteAsync(string inputPath, string outputPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(inputPath, outputPath, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The DOL size (Dolphin: GetBootDOLSize): the largest end offset of the seven text and
    /// eleven data segments in the DOL header, or 0 when the DOL is absent or unreadable.
    /// </summary>
    private static uint GetDolSize(IBlobReader input, uint dolOffset)
    {
        if (dolOffset == 0)
        {
            return 0;
        }

        uint size = 0;
        Span<byte> offsetBytes = stackalloc byte[4];
        Span<byte> sizeBytes = stackalloc byte[4];
        for (var i = 0; i < 18; i++)
        {
            var offsetField = i < 7 ? 0x00 + i * 4 : 0x1C + (i - 7) * 4;
            var sizeField = i < 7 ? 0x90 + i * 4 : 0xAC + (i - 7) * 4;
            if (!TryReadExactly(input, dolOffset + (uint)offsetField, offsetBytes) ||
                !TryReadExactly(input, dolOffset + (uint)sizeField, sizeBytes))
            {
                return 0;
            }

            size = Math.Max(size, ReadBe32(offsetBytes, 0) + ReadBe32(sizeBytes, 0));
        }

        return size;
    }

    private static bool TryReadExactly(IBlobReader input, long offset, Span<byte> buffer)
    {
        return offset >= 0 && offset + buffer.Length <= input.Length
                           && input.ReadAt(offset, buffer) == buffer.Length;
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
