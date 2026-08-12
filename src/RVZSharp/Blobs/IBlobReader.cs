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
}
