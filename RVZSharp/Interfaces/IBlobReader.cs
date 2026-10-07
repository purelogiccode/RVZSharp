using RVZSharp.Models;

namespace RVZSharp.Interfaces;

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
    /// <param name="position">Offset into the decoded disc image.</param>
    /// <param name="buffer">The buffer to fill; its length is the number of bytes to read.</param>
    /// <returns>The number of bytes read (fewer at the end of the image).</returns>
    int ReadAt(long position, Span<byte> buffer);

    /// <summary>
    /// Decodes the entire disc image into a byte array. For large images prefer streaming
    /// with <see cref="CopyTo"/> so the image is never fully resident in memory.
    /// </summary>
    /// <exception cref="RvzFormatException">
    /// The image is larger than 2 GiB (use <see cref="CopyTo"/> for images that large).
    /// </exception>
    byte[] ReadFully()
    {
        return ReadFully(null, default);
    }

    /// <summary>
    /// Decodes the entire disc image into a byte array, reporting progress and observing
    /// cancellation. For large images prefer streaming with <see cref="CopyTo"/>.
    /// </summary>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The decoded disc image bytes.</returns>
    /// <exception cref="RvzFormatException">
    /// The image is larger than 2 GiB (use <see cref="CopyTo"/> for images that large), or
    /// decoding stopped before the end of the image.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    byte[] ReadFully(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (Length > int.MaxValue)
        {
            throw new RvzFormatException(
                $"The image is {Length} bytes; ReadFully supports at most {int.MaxValue} bytes — "
                + "stream it with CopyTo instead.");
        }

        var result = new byte[Length];
        using var destination = new MemoryStream(result, writable: true);
        CopyTo(destination, progress, cancellationToken);
        return result;
    }

    /// <summary>
    /// Streams the decoded disc image into <paramref name="destination"/> in bounded blocks
    /// (1 MiB per read), reporting progress and observing cancellation. Unlike
    /// <see cref="ReadFully()"/>, this supports images of any size.
    /// </summary>
    /// <param name="destination">The stream that receives the decoded image bytes.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The number of bytes copied (the image length).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="RvzFormatException">Decoding stopped before the end of the image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    long CopyTo(Stream destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return IO.BlobCopy.CopyTo(this, destination, progress, cancellationToken);
    }
}
