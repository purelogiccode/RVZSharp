using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>
/// The CISO / WBI format (Dolphin: CISOFileReader): a 0x8000-byte header holding a presence
/// map, then the used blocks (of a fixed size) stored sequentially. Absent blocks decode to
/// zeroes. The block size and map entries are little endian; the map marks 1 = present.
/// </summary>
public sealed class CisoBlob : IBlobReader
{
    /// <summary>Size in bytes of the CISO header, including the block presence map.</summary>
    public const int HeaderSize = 0x8000;

    /// <summary>Number of map entries (and thus of blocks in the decoded image).</summary>
    public const int MapSize = HeaderSize - 8;

    private const uint UsedBlock = 1;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly ushort[] _blockMap; // sequential index of each present block, or 0xFFFF

    private CisoBlob(Stream file, bool leaveOpen, int blockSize, ushort[] blockMap)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        BlockSize = blockSize;
        _blockMap = blockMap;
        Length = (long)MapSize * blockSize;
    }

    /// <summary>The CISO blob type.</summary>
    public BlobType Type => BlobType.Ciso;

    /// <summary>Size in bytes of the decoded disc image (map size times block size).</summary>
    public long Length { get; }

    /// <summary>Size in bytes of one CISO block, read from the file header.</summary>
    public int BlockSize { get; }

    /// <summary>Parses a CISO file. The stream must be seekable.</summary>
    public static CisoBlob Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The CISO stream must be seekable.", nameof(stream));
        }

        if (stream.Length < HeaderSize)
        {
            throw new RvzFormatException("The file is too short to contain a CISO header.");
        }

        Span<byte> magic = stackalloc byte[4];
        if (!ReadExactlyAt(stream, 0, magic) || !magic.SequenceEqual("CISO"u8))
        {
            throw new RvzFormatException(
                $"Bad CISO magic: expected \"CISO\", got {System.Text.Encoding.ASCII.GetString(magic)}.");
        }

        Span<byte> blockSizeBytes = stackalloc byte[4];
        ReadExactlyAt(stream, 4, blockSizeBytes);
        var blockSize = (int)ReadLe32(blockSizeBytes, 0);
        if (blockSize <= 0)
        {
            throw new RvzFormatException($"Invalid CISO block size {blockSize}.");
        }

        var mapBytes = new byte[MapSize];
        if (!ReadExactlyAt(stream, 8, mapBytes))
        {
            throw new RvzFormatException("The CISO block map is truncated.");
        }

        var map = new ushort[MapSize];
        ushort usedCount = 0;
        for (var i = 0; i < MapSize; i++)
        {
            // Dolphin treats anything other than exactly 1 as absent.
            map[i] = mapBytes[i] == UsedBlock ? usedCount++ : (ushort)0xFFFF;
        }

        return new CisoBlob(stream, leaveOpen, blockSize, map);
    }

    /// <summary>
    /// Reads up to buffer.Length bytes at position into buffer, decoding CISO blocks from the
    /// file (absent blocks are served as zeroes); returns the number of bytes read, 0 at the
    /// end of the image.
    /// </summary>
    /// <param name="position">Offset in the decoded image to read from.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <returns>The number of bytes read; 0 when position is at or past the end of the image.</returns>
    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var block = (int)(position / BlockSize);
            var offsetInBlock = (int)(position % BlockSize);
            var take = (int)Math.Min(Math.Min(buffer.Length, BlockSize - offsetInBlock), Length - position);

            var mapEntry = _blockMap[block];
            if (mapEntry == 0xFFFF)
            {
                buffer[..take].Clear(); // absent block → zeroes
            }
            else
            {
                var fileOffset = HeaderSize + (long)mapEntry * BlockSize + offsetInBlock;
                if (!ReadExactlyAt(_file, fileOffset, buffer[..take]))
                {
                    throw new RvzFormatException($"CISO block {block} is truncated.");
                }
            }

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    private static uint ReadLe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
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
