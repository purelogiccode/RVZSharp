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

    public static PlainBlob Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        return new PlainBlob(stream, leaveOpen);
    }

    public BlobType Type => BlobType.Plain;
    public long Length { get; }
    public int BlockSize => 0;

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

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _file.Dispose();
        }
    }
}
