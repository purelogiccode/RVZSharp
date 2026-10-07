using RVZSharp.Interfaces;

namespace RVZSharp.IO;

/// <summary>
/// Shared implementation of <see cref="IBlobReader.CopyTo"/>: streams a decoded disc image
/// into a destination stream in bounded 1 MiB blocks, reporting progress and observing
/// cancellation between blocks.
/// </summary>
internal static class BlobCopy
{
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Copies the decoded image served by <paramref name="reader"/> into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="reader">The decoded disc image source.</param>
    /// <param name="destination">The stream that receives the image bytes.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The number of bytes copied (the image length).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="RvzFormatException">Decoding stopped before the end of the image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static long CopyTo(IBlobReader reader, Stream destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (reader.Length <= 0)
        {
            return 0;
        }

        var buffer = new byte[(int)Math.Min(BufferSize, reader.Length)];
        var copied = 0L;
        while (copied < reader.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(buffer.Length, reader.Length - copied);
            var read = reader.ReadAt(copied, buffer.AsSpan(0, take));
            if (read <= 0)
            {
                throw new RvzFormatException($"Decoding stopped at offset 0x{copied:X}.");
            }

            destination.Write(buffer, 0, read);
            copied += read;
            progress?.Report((double)copied / reader.Length);
        }

        return copied;
    }
}
