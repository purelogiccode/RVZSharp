namespace RVZSharp.Blobs;

/// <summary>
/// A read-only disc image container (Dolphin: BlobReader). Implementations decode any of the
/// supported formats on the fly and serve the original disc image bytes via <see cref="ReadAt"/>.
/// </summary>
public interface IBlobReader : IDisposable
{
    /// <summary>The detected container format.</summary>
    BlobType Type { get; }

    /// <summary>Size of the decoded disc image in bytes.</summary>
    long Length { get; }

    /// <summary>Block size in bytes, or 0 for formats without blocks.</summary>
    int BlockSize { get; }

    /// <summary>
    /// Reads <paramref name="buffer.Length"/> bytes of the decoded disc image at
    /// <paramref name="position"/>. Returns fewer bytes at the end of the image.
    /// </summary>
    int ReadAt(long position, Span<byte> buffer);

    /// <summary>
    /// Decodes the entire disc image into a byte array. For large images prefer streaming
    /// with <see cref="ReadAt"/> so the image is never fully resident in memory.
    /// </summary>
    /// <exception cref="RvzFormatException">
    /// The image is larger than 2 GiB (use <see cref="ReadAt"/> for images that large).
    /// </exception>
    byte[] ReadFully()
    {
        if (Length > int.MaxValue)
        {
            throw new RvzFormatException(
                $"The image is {Length} bytes; ReadFully supports at most {int.MaxValue} bytes — "
                + "stream it with ReadAt instead.");
        }

        var result = new byte[Length];
        var position = 0;
        while (position < result.Length)
        {
            var read = ReadAt(position, result.AsSpan(position));
            if (read <= 0)
            {
                throw new RvzFormatException($"Decoding stopped at offset 0x{position:X}.");
            }

            position += read;
        }

        return result;
    }
}
