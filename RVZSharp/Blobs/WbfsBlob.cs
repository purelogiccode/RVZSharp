using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>
/// A standalone WBFS file (Dolphin: WbfsFileReader): a 512-byte header (magic, hard-drive
/// sector size/count, WBFS cluster shift, disc table) followed by the WBFS cluster allocation
/// table (u16 big-endian disc-cluster → volume-cluster map) and the volume data. Disc sectors
/// not backed by a volume cluster cannot be read; the decoded size is the fixed Wii double
/// layer size (upper bound).
/// </summary>
public sealed class WbfsBlob : IBlobReader
{
    private const int HeaderSize = 512;
    private const int DiscHeaderSize = 256;
    private const int WiiSectorSize = 0x8000;

    /// <summary>Fixed decoded size: 143432 × 2 sectors of 32 KiB (Dolphin: WII_SECTOR_COUNT).</summary>
    public static readonly long WiiDataSize = 143432L * 2 * WiiSectorSize;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly long _hdSectorSize;
    private readonly long _clusterSize;
    private readonly ushort[] _wlbaTable;
    private readonly long _blocksPerDisc;

    private WbfsBlob(Stream file, bool leaveOpen, long hdSectorSize, long clusterSize,
        ushort[] wlbaTable, long blocksPerDisc)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        _hdSectorSize = hdSectorSize;
        _clusterSize = clusterSize;
        _wlbaTable = wlbaTable;
        _blocksPerDisc = blocksPerDisc;
        Length = WiiDataSize;
    }

    public BlobType Type => BlobType.Wbfs;
    public long Length { get; }
    public int BlockSize => (int)_clusterSize;

    /// <summary>
    /// Parses a .wbfs file (disc slot 0). When <paramref name="filePath"/> is given, the
    /// split parts (game.wbf1, game.wbf2, ...) are opened like Dolphin (WbfsBlob.cpp:32-33,
    /// 62-79) and the declared size is checked against the sum of all parts. The stream must
    /// be seekable.
    /// </summary>
    public static WbfsBlob Open(Stream stream, string? filePath = null, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The WBFS stream must be seekable.", nameof(stream));
        }

        var header = new byte[HeaderSize];
        if (!ReadExactlyAt(stream, 0, header))
        {
            throw new RvzFormatException("The file is too short to contain a WBFS header.");
        }

        if (!header.AsSpan(0, 4).SequenceEqual("WBFS"u8))
        {
            throw new RvzFormatException(
                $"Bad WBFS magic: expected \"WBFS\", got {System.Text.Encoding.ASCII.GetString(header, 0, 4)}.");
        }

        var hdSectorCount = (long)ReadBe32(header, 4);
        var hdSectorSize = 1L << header[8];
        var clusterSize = 1L << header[9];

        // Dolphin replaces the last path character with the part index: game.wbfs, game.wbf1,
        // game.wbf2, ... and checks the declared size against the SUM of all parts.
        var parts = new List<Stream> { stream };
        if (filePath is { Length: > 0 })
        {
            var chars = filePath.ToCharArray();
            for (var i = 1; i < 10; i++)
            {
                chars[^1] = (char)('0' + i);
                var partPath = new string(chars);
                if (!File.Exists(partPath))
                {
                    break;
                }

                parts.Add(File.OpenRead(partPath));
            }
        }

        long totalLength = 0;
        foreach (var part in parts)
        {
            totalLength += part.Length;
        }

        if (hdSectorCount * hdSectorSize != totalLength)
        {
            foreach (var part in parts.Skip(1))
            {
                part.Dispose();
            }

            throw new RvzFormatException(
                $"WBFS size mismatch: header declares {hdSectorCount * hdSectorSize} bytes, "
                + $"actual {totalLength} ({parts.Count} part(s)).");
        }

        var file = parts.Count == 1 ? stream : new MultiPartStream(parts, leaveOpen);

        if (clusterSize < WiiSectorSize)
        {
            throw new RvzFormatException($"Invalid WBFS cluster size {clusterSize} (must be ≥ 32 KiB).");
        }

        // The header is magic(0-3), hd_sector_count(4-7), hd_sector_shift(8),
        // wbfs_sector_shift(9), padding(10-11), disc_table[500](12-511) (Dolphin:
        // WbfsBlob.h WbfsHeader). Dolphin requires disc_table[0] != 0 (WbfsBlob.cpp:119).
        if (header[12] == 0) // disc_table[0]
        {
            throw new RvzFormatException("The WBFS file does not contain a disc in slot 0.");
        }

        var blocksPerDisc = (WiiDataSize + clusterSize - 1) / clusterSize;
        var tableOffset = hdSectorSize + DiscHeaderSize;
        if (tableOffset + blocksPerDisc * 2 > file.Length)
        {
            throw new RvzFormatException("The WBFS disc table is truncated.");
        }

        var table = new ushort[blocksPerDisc];
        Span<byte> entry = stackalloc byte[2];
        for (var i = 0; i < blocksPerDisc; i++)
        {
            if (!ReadExactlyAt(file, tableOffset + i * 2L, entry))
            {
                throw new RvzFormatException("The WBFS disc table is truncated.");
            }

            table[i] = (ushort)((entry[0] << 8) | entry[1]);
        }

        return new WbfsBlob(file, leaveOpen, hdSectorSize, clusterSize, table, blocksPerDisc);
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
            var cluster = (int)(position >> ShiftOf(_clusterSize));
            if (cluster >= _blocksPerDisc)
            {
                throw new RvzFormatException(
                    $"WBFS read beyond the end of the disc at 0x{position:X}.");
            }

            var clusterOffset = (int)(position & (_clusterSize - 1));
            var take = (int)Math.Min(Math.Min(buffer.Length, _clusterSize - clusterOffset), Length - position);

            var address = _wlbaTable[cluster] * _clusterSize + clusterOffset;
            if (address + take > _file.Length)
            {
                throw new RvzFormatException(
                    $"WBFS disc cluster {cluster} is not allocated (or the file is truncated).");
            }

            if (!ReadExactlyAt(_file, address, buffer[..take]))
            {
                throw new RvzFormatException($"WBFS read failed at 0x{position:X}.");
            }

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    private static int ShiftOf(long value)
    {
        var shift = 0;
        while ((1L << shift) < value)
        {
            shift++;
        }

        return shift;
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
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
        if (_file is MultiPartStream split)
        {
            // The wrapper applies leaveOpen to the first part and always closes the
            // continuation parts it opened.
            split.Dispose();
        }
        else if (!_leaveOpen)
        {
            _file.Dispose();
        }
    }

    /// <summary>
    /// Concatenates the parts of a split WBFS image (game.wbfs + game.wbf1 + ...) into one
    /// seekable stream. Owns the continuation parts; the first part follows the caller's
    /// leaveOpen choice.
    /// </summary>
    private sealed class MultiPartStream : Stream
    {
        private readonly Stream[] _parts;
        private readonly long[] _starts;
        private readonly bool _leaveOpen;
        private long _position;

        public MultiPartStream(List<Stream> parts, bool leaveOpen)
        {
            _parts = parts.ToArray();
            _leaveOpen = leaveOpen;
            _starts = new long[parts.Count];
            var running = 0L;
            for (var i = 0; i < parts.Count; i++)
            {
                _starts[i] = running;
                running += parts[i].Length;
            }

            Length = running;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count && _position < Length)
            {
                var partIndex = Array.BinarySearch(_starts, _position);
                if (partIndex < 0)
                {
                    partIndex = ~partIndex - 1;
                }

                var part = _parts[partIndex];
                var local = _position - _starts[partIndex];
                if (part.Position != local)
                {
                    part.Position = local;
                }

                var take = (int)Math.Min(count - total, part.Length - local);
                var read = part.Read(buffer, offset + total, take);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                _position += read;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset
            };
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (var i = _leaveOpen ? 1 : 0; i < _parts.Length; i++)
                {
                    _parts[i].Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
