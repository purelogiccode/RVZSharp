using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>A plain, uncompressed disc image. Serves the file bytes directly.</summary>
public sealed class PlainBlob : IBlobReader
{
    private readonly Stream _file;
    private readonly bool _leaveOpen;

    private PlainBlob(Stream file, bool leaveOpen)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        Length = file.Length;
    }

    /// <summary>Opens a plain uncompressed disc image. The stream must be seekable.</summary>
    public static PlainBlob Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        return new PlainBlob(stream, leaveOpen);
    }

    /// <summary>The plain (uncompressed ISO) blob type.</summary>
    public BlobType Type => BlobType.Plain;

    /// <summary>Size of the file in bytes; the decoded image is the file itself.</summary>
    public long Length { get; }

    /// <summary>The image has no block structure; always 0.</summary>
    public int BlockSize => 0;

    /// <summary>
    /// Reads up to buffer.Length bytes at position directly from the file into buffer;
    /// returns the number of bytes read, 0 at the end of the image.
    /// </summary>
    /// <param name="position">Offset in the image to read from.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <returns>The number of bytes read; 0 when position is at or past the end of the image.</returns>
    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var take = (int)Math.Min(buffer.Length, Length - position);
        return ReadExactlyAt(_file, position, buffer[..take]) ? take : 0;
    }

    private static bool ReadExactlyAt(Stream stream, long position, Span<byte> buffer)
    {
        if (stream.Position != position)
        {
            stream.Position = position;
        }

        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>Disposes the underlying file stream, unless leaveOpen was set.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _file.Dispose();
        }
    }
}
