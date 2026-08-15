using System.IO.Compression;
using RVZSharp.IO;

namespace RVZSharp.Blobs;

/// <summary>
/// Dolphin's GCZ format (Dolphin: CompressedBlobReader): a header, a table of block pointers
/// (the top bit marks a stored-uncompressed block), per-block Adler-32 hashes of the stored
/// bytes, and zlib-compressed blocks of 16 KiB. All header integers are little endian.
/// </summary>
public sealed class GczBlob : IBlobReader
{
    /// <summary>The 4 magic bytes at the start of the file (Dolphin: GCZ_MAGIC, stored little endian).</summary>
    public static ReadOnlySpan<byte> Magic => [0x01, 0xC0, 0x0B, 0xB1];

    private const ulong UncompressedFlag = 1UL << 63;
    private const int HeaderSize = 32;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly long _dataOffset;
    private readonly long _compressedDataSize;
    private readonly long[] _blockOffsets;
    private readonly bool[] _blockCompressed;
    private readonly uint[] _blockHashes;
    private readonly int _blockSize;

    private GczBlob(Stream file, bool leaveOpen, long dataOffset, long compressedDataSize,
        long[] blockOffsets, bool[] blockCompressed, uint[] blockHashes, int blockSize, long length)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        _dataOffset = dataOffset;
        _compressedDataSize = compressedDataSize;
        _blockOffsets = blockOffsets;
        _blockCompressed = blockCompressed;
        _blockHashes = blockHashes;
        _blockSize = blockSize;
        Length = length;
    }

    /// <summary>Number of blocks in the file.</summary>
    public long NumBlocks => _blockOffsets.Length;

    public BlobType Type => BlobType.Gcz;
    public long Length { get; }
    public int BlockSize => _blockSize;

    /// <summary>Parses and validates a GCZ file. The stream must be seekable.</summary>
    public static GczBlob Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The GCZ stream must be seekable.", nameof(stream));
        }

        Span<byte> headerBytes = stackalloc byte[HeaderSize];
        if (!ReadExactlyAt(stream, 0, headerBytes))
        {
            throw new RvzFormatException("The file is too short to contain a GCZ header.");
        }

        if (ReadLe32(headerBytes, 0) != 0xB10BC001)
        {
            throw new RvzFormatException(
                $"Bad GCZ magic: expected 0xB10BC001, got 0x{ReadLe32(headerBytes, 0):X8}.");
        }

        var compressedDataSize = ReadLe64(headerBytes, 8);
        var discSize = ReadLe64(headerBytes, 16);
        var blockSize = ReadLe32(headerBytes, 24);
        var numBlocks = ReadLe32(headerBytes, 28);

        if (numBlocks == 0)
        {
            throw new RvzFormatException("The GCZ file has zero blocks.");
        }

        if (blockSize == 0)
        {
            throw new RvzFormatException("The GCZ block size is zero.");
        }

        // Reject sizes that would overflow the int casts used for reads and allocations
        // (hostile headers only; Dolphin's own converter writes 0x4000-byte blocks).
        if (blockSize > int.MaxValue)
        {
            throw new RvzFormatException($"The GCZ block size {blockSize} is too large.");
        }

        // Compare in ulong so a header with the top bit set cannot wrap past the bounds check.
        var headerSize = HeaderSize + (ulong)numBlocks * 12;
        if (headerSize > (ulong)stream.Length ||
            compressedDataSize > (ulong)stream.Length - headerSize)
        {
            throw new RvzFormatException("The GCZ header or data area is larger than the file.");
        }

        var offsets = new long[numBlocks];
        var compressed = new bool[numBlocks];
        var hashes = new uint[numBlocks];
        var pointerTableSize = numBlocks * 8L;
        Span<byte> entry = stackalloc byte[8];
        for (var i = 0; i < numBlocks; i++)
        {
            if (!ReadExactlyAt(stream, HeaderSize + i * 8L, entry))
            {
                throw new RvzFormatException("The GCZ block table is truncated.");
            }

            var pointer = ReadLe64(entry, 0);
            offsets[i] = (long)(pointer & ~UncompressedFlag);
            compressed[i] = (pointer & UncompressedFlag) == 0;

            if (offsets[i] > (long)compressedDataSize)
            {
                throw new RvzFormatException($"GCZ block {i} points past the data area.");
            }
        }

        for (var i = 0; i < numBlocks; i++)
        {
            if (!ReadExactlyAt(stream, HeaderSize + pointerTableSize + i * 4L, entry[..4]))
            {
                throw new RvzFormatException("The GCZ hash table is truncated.");
            }

            hashes[i] = ReadLe32(entry, 0);
        }

        // Validate every block's stored size against the next block's offset
        // (Dolphin: ValidateBlockPointers).
        for (var i = 0; i < numBlocks; i++)
        {
            var next = i + 1 < numBlocks ? offsets[i + 1] : (long)compressedDataSize;
            var size = next - offsets[i];
            if (size < 0)
            {
                throw new RvzFormatException($"GCZ block pointers {i} and {i + 1} are out of order.");
            }

            if (compressed[i])
            {
                if (size > blockSize + 64)
                {
                    throw new RvzFormatException($"GCZ block {i} is too large.");
                }
            }
            else if (size != blockSize)
            {
                throw new RvzFormatException($"GCZ block {i} is stored uncompressed with the wrong size.");
            }
        }

        // headerSize was validated against stream.Length, so it fits in long.
        return new GczBlob(stream, leaveOpen, (long)headerSize, (long)compressedDataSize, offsets,
            compressed, hashes, (int)blockSize, (long)discSize);
    }

    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var blockIndex = (int)(position / _blockSize);
            if (blockIndex >= _blockOffsets.Length)
            {
                // The header's disc_size exceeds the block table: reading past the last
                // block is a format error (Dolphin fails the read, CompressedBlob.cpp:137-138).
                throw new RvzFormatException(
                    $"GCZ read at 0x{position:X} is past the last block (disc_size exceeds the block table).");
            }

            var offsetInBlock = (int)(position % _blockSize);
            var take = (int)Math.Min(Math.Min(buffer.Length, _blockSize - offsetInBlock), Length - position);

            var block = DecodeBlock(blockIndex);
            block.AsSpan(offsetInBlock, take).CopyTo(buffer);

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    private byte[] DecodeBlock(int blockIndex)
    {
        var start = _dataOffset + _blockOffsets[blockIndex];
        var end = blockIndex + 1 < _blockOffsets.Length
            ? _dataOffset + _blockOffsets[blockIndex + 1]
            : _dataOffset + _compressedDataSize;
        var storedSize = (int)(end - start);

        var stored = new byte[storedSize];
        if (!ReadExactlyAt(_file, start, stored))
        {
            throw new RvzFormatException($"GCZ block {blockIndex} is truncated.");
        }

        if (Adler32.Compute(stored) != _blockHashes[blockIndex])
        {
            throw new RvzHashMismatchException($"GCZ block {blockIndex} failed its Adler-32 check.");
        }

        if (!_blockCompressed[blockIndex])
        {
            return stored;
        }

        byte[] output;
        try
        {
            using var compressed = new MemoryStream(stored, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            output = new byte[_blockSize];
            var total = 0;
            while (total < _blockSize)
            {
                var read = zlib.Read(output, total, _blockSize - total);
                if (read <= 0)
                {
                    throw new RvzFormatException(
                        $"GCZ block {blockIndex} decompressed to {total} bytes, expected {_blockSize}.");
                }

                total += read;
            }
        }
        catch (InvalidDataException e)
        {
            throw new RvzFormatException($"GCZ block {blockIndex} is not valid zlib data.", e);
        }

        return output;
    }

    private static uint ReadLe32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static ulong ReadLe64(ReadOnlySpan<byte> data, int offset) =>
        ReadLe32(data, offset) | ((ulong)ReadLe32(data, offset + 4) << 32);

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
