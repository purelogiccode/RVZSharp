using System.Buffers.Binary;
using RVZSharp.IO;
using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>
/// The GameCube TGC format (Dolphin: TGCFileReader): the file starts with a 56-byte
/// big-endian header, followed by the disc image data. The real DOL and FST offsets stored in
/// the disc header are rewritten to their ISO-relative positions, and the FST's file offsets
/// are relocated by the difference between the file area's real and virtual offsets.
/// </summary>
public sealed class TgcBlob : IBlobReader
{
    public const int HeaderStructSize = 56;

    private const uint MagicValue = 0xA2380FAE;
    private const int DolOffsetField = 0x0420;
    private const int FstOffsetField = 0x0424;
    private const int FstEntrySize = 12;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly uint _tgcHeaderSize;
    private readonly uint _dolRealOffset;
    private readonly uint _fstRealOffset;
    private readonly uint _replacementFstOffset;
    private readonly byte[] _patchedFst;

    private TgcBlob(Stream file, bool leaveOpen, uint tgcHeaderSize, uint dolRealOffset,
        uint fstRealOffset, uint replacementFstOffset, byte[] patchedFst)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        _tgcHeaderSize = tgcHeaderSize;
        _dolRealOffset = dolRealOffset;
        _fstRealOffset = fstRealOffset;
        _replacementFstOffset = replacementFstOffset;
        _patchedFst = patchedFst;
        Length = file.Length - tgcHeaderSize;
    }

    public BlobType Type => BlobType.Tgc;
    public long Length { get; }
    public int BlockSize => 0;

    /// <summary>Parses a TGC file. The stream must be seekable.</summary>
    public static TgcBlob Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The TGC stream must be seekable.", nameof(stream));
        }

        if (stream.Length < HeaderStructSize)
        {
            throw new RvzFormatException("The file is too short to contain a TGC header.");
        }

        Span<byte> header = stackalloc byte[HeaderStructSize];
        if (!ReadExactlyAt(stream, 0, header))
        {
            throw new RvzFormatException("The TGC header is truncated.");
        }

        // The magic is the one little-endian field in the TGC header (Dolphin compares a
        // native u32 read against TGC_MAGIC = 0xA2380FAE, TGCBlob.cpp:50); every other field
        // is big-endian and stays on ReadBe32 below.
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != MagicValue)
        {
            throw new RvzFormatException(
                $"Bad TGC magic: expected 0x{MagicValue:X8}, got 0x{BinaryPrimitives.ReadUInt32LittleEndian(header):X8}.");
        }

        var tgcHeaderSize = ReadBe32(header, 8);
        var fstRealOffset = ReadBe32(header, 16);
        var fstSize = ReadBe32(header, 20);
        var dolRealOffset = ReadBe32(header, 28);
        var fileAreaRealOffset = ReadBe32(header, 36);
        var fileAreaVirtualOffset = ReadBe32(header, 52);

        if (tgcHeaderSize > stream.Length)
        {
            throw new RvzFormatException($"Invalid TGC header size {tgcHeaderSize}.");
        }

        // The FST is stored in the file at fst_real_offset; on read failure Dolphin tolerates
        // an empty FST, so do the same. Clamp hostile sizes so a bad header cannot allocate
        // gigabytes, and use the clamped size for every subsequent bound.
        var fstSizeClamped = (int)Math.Min(fstSize, Math.Max(0, stream.Length - fstRealOffset));
        var rawFst = new byte[fstSizeClamped];
        var haveFst = ReadExactlyAt(stream, fstRealOffset, rawFst);

        // Relocate every file entry's offset from file-relative to ISO-relative. The shift can
        // overflow u32; Dolphin relies on the wrap cancelling out, so use unchecked arithmetic.
        var fileAreaShift = unchecked(fileAreaRealOffset - fileAreaVirtualOffset - tgcHeaderSize);
        // C++: when the FST read fails, m_fst is cleared and nothing is substituted
        // (TGCBlob.cpp:66-70) — the FST region keeps the file's raw bytes.
        var patchedFst = haveFst ? rawFst : [];
        if (haveFst && fstSizeClamped >= FstEntrySize)
        {
            var claimedEntries = ReadBe32(rawFst, 8);
            var entryCount = Math.Min(claimedEntries, fstSizeClamped / FstEntrySize);
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = i * FstEntrySize;
                if (rawFst[entryOffset] == 0) // a file (as opposed to a directory)
                {
                    var oldOffset = ReadBe32(rawFst, entryOffset + 4);
                    WriteBe32(rawFst, entryOffset + 4, unchecked(oldOffset + fileAreaShift));
                }
            }

            patchedFst = rawFst;
        }

        return new TgcBlob(stream, leaveOpen, tgcHeaderSize, dolRealOffset, fstRealOffset,
            unchecked(fstRealOffset - tgcHeaderSize), patchedFst);
    }

    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var take = (int)Math.Min(buffer.Length, Length - position);
        if (!ReadExactlyAt(_file, position + _tgcHeaderSize, buffer[..take]))
        {
            throw new RvzFormatException($"TGC read failed at 0x{position:X}.");
        }

        // Rewrite the DOL and FST offsets in the disc header and replace the ISO-relative FST
        // with the relocated FST — only where the requested range intersects the patches.
        ReplaceBe32(buffer, position, take, DolOffsetField,
            unchecked(_dolRealOffset - _tgcHeaderSize));
        ReplaceBe32(buffer, position, take, FstOffsetField,
            unchecked(_fstRealOffset - _tgcHeaderSize));
        ReplaceRange(buffer, position, take, _replacementFstOffset, _patchedFst);

        return take;
    }

    private static void ReplaceBe32(Span<byte> output, long readOffset, int readSize,
        long replaceOffset, uint value)
    {
        var start = Math.Max(readOffset, replaceOffset);
        var end = Math.Min(readOffset + readSize, replaceOffset + 4);
        if (end <= start)
        {
            return;
        }

        var dest = (int)(start - readOffset);
        var valueByteStart = (int)(start - replaceOffset); // 0..3 within the value
        for (var i = 0; i < end - start; i++)
        {
            output[dest + i] = (byte)(value >> (24 - 8 * (valueByteStart + i)));
        }
    }

    private static void ReplaceRange(Span<byte> output, long readOffset, int readSize,
        long replaceOffset, ReadOnlySpan<byte> replacement)
    {
        var start = Math.Max(readOffset, replaceOffset);
        var end = Math.Min(readOffset + readSize, replaceOffset + replacement.Length);
        if (end <= start)
        {
            return;
        }

        replacement.Slice((int)(start - replaceOffset), (int)(end - start))
            .CopyTo(output[(int)(start - readOffset)..]);
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteBe32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
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
