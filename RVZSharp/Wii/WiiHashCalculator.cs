using System.Security.Cryptography;
using RVZSharp.Models;

namespace RVZSharp.Wii;

/// <summary>
/// Builds the per-sector hash area (0x400 bytes) of a Wii disc sector, following Dolphin's
/// VolumeWii::HashGroup layout:
/// 0x000-0x26C h0 (31 × SHA-1 of each 0x400 data block), 0x26C-0x280 padding,
/// 0x280-0x320 h1 (8 × SHA-1 of the sector's h0 array, same in all 8 sectors of a group),
/// 0x320-0x340 padding, 0x340-0x3E0 h2 (8 × SHA-1 of each group's h1 array, same in all
/// 64 sectors), 0x3E0-0x400 padding.
/// </summary>
public static class WiiHashCalculator
{
    public const int BlockDataSize = 0x400;
    public const int BlocksPerSector = 31;
    public const int SectorDataSize = BlocksPerSector * BlockDataSize; // 0x7C00
    public const int HashBlockSize = 0x400;
    public const int HashSize = 20;
    public const int H0Size = BlocksPerSector * HashSize; // 0x26C
    private const int H0Padding = 0x14;
    private const int H1Size = 8 * HashSize; // 0xA0
    private const int H1Padding = 0x20;
    private const int H2Size = 8 * HashSize; // 0xA0
    private const int H2Padding = 0x20;

    /// <summary>
    /// The h0 area (31 × SHA-1 of a 0x400 block) of a sector whose data is all zeroes — what
    /// sectors beyond a partition's data end hash to. Dolphin zero-fills the sector data and
    /// hashes it normally (VolumeWii.cpp EncryptGroup: unencrypted_data[block].fill(0)); Go's
    /// reader does the same by reading zeros into the cluster (part.go readGroup). This is a
    /// constant, so it is computed once.
    /// </summary>
    public static readonly byte[] ZeroSectorH0Area = ComputeZeroSectorH0Area();

    private static byte[] ComputeZeroSectorH0Area()
    {
        var hashArea = new byte[HashBlockSize];
        BuildHashArea(new byte[SectorDataSize], hashArea);
        return hashArea[..H0Size].ToArray();
    }

    /// <summary>
    /// Computes the 0x400-byte hash area for one sector (before applying hash exceptions).
    /// <paramref name="hashArea"/> must be exactly <see cref="HashBlockSize"/> bytes.
    /// </summary>
    public static void BuildHashArea(ReadOnlySpan<byte> sectorData, Span<byte> hashArea)
    {
        if (sectorData.Length != SectorDataSize)
        {
            throw new ArgumentException($"Sector data must be {SectorDataSize} bytes.", nameof(sectorData));
        }

        if (hashArea.Length != HashBlockSize)
        {
            throw new ArgumentException($"Hash area must be {HashBlockSize} bytes.", nameof(hashArea));
        }

        hashArea.Clear();
        Span<byte> hash = stackalloc byte[HashSize];

        // H0 hashes: SHA-1 of each 0x400 data block.
        for (var i = 0; i < BlocksPerSector; i++)
        {
            SHA1.HashData(sectorData.Slice(i * BlockDataSize, BlockDataSize), hash);
            hash.CopyTo(hashArea[(i * HashSize)..]);
        }

        // H0 padding (0x26C-0x280) is already zero.

        // H1 hash of this sector's h0 array (goes into slot 0 of the group's shared h1).
        SHA1.HashData(hashArea[..H0Size], hash);
        hash.CopyTo(hashArea[(H0Size + H0Padding)..]);

        // H1 padding (0x320-0x340) is already zero.

        // H2 hash of this sector's h1 array (goes into slot (sector / 8) of the shared h2).
        SHA1.HashData(hashArea.Slice(H0Size + H0Padding, H1Size), hash);
        hash.CopyTo(hashArea[(H0Size + H0Padding + H1Size + H1Padding)..]);

        // H2 padding (0x3E0-0x400) is already zero.
    }

    /// <summary>
    /// Applies hash exceptions to a 0x400-byte hash area. <paramref name="chunkBaseOffset"/> is
    /// the exception offset of the first byte of this hash area within the chunk's data
    /// (usually 0; used when a chunk covers multiple 2 MiB exception regions).
    /// </summary>
    public static void ApplyHashExceptions(ReadOnlySpan<HashExceptionEntry> exceptions,
        Span<byte> hashArea, int chunkBaseOffset = 0)
    {
        foreach (var exception in exceptions)
        {
            var offset = chunkBaseOffset + exception.Offset;
            var offsetInBlock = offset & 0x3FF;

            // A per-sector exception must fit inside this sector's 0x400 hash area; anything
            // beyond it is malformed (Dolphin: ApplyHashExceptions, WIABlob.cpp:868-876).
            if (offset >= HashBlockSize || offsetInBlock + HashSize > HashBlockSize)
            {
                throw new RvzFormatException(
                    $"Hash exception at offset 0x{exception.Offset:X4} is outside the sector hash area.");
            }

            exception.Hash.CopyTo(hashArea[offsetInBlock..]);
        }
    }
}
