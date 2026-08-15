using System.Security.Cryptography;
using RVZSharp.Models;

namespace RVZSharp.Wii;

/// <summary>
/// Reads the encrypted partition data of one 2 MiB region from the input image, decrypts the
/// data and hash areas (AES-128-CBC, IV = ciphertext at 0x3D0 for data, zero IV for hashes),
/// recalculates the h0/h1/h2 hash tree the same way the reader rebuilds it, and returns the
/// decrypted data plus the hash exceptions (every 20-byte hash that differs from the
/// original, with chunk-relative offsets — Dolphin: ProcessAndCompress).
/// </summary>
public sealed class WiiPartitionExtractor
{
    private readonly Interfaces.IBlobReader _input;
    private readonly byte[] _key;
    private readonly Aes _aes;

    public WiiPartitionExtractor(Interfaces.IBlobReader input, ReadOnlySpan<byte> key)
    {
        _input = input;
        _key = key.ToArray();
        _aes = Aes.Create();
        _aes.Key = _key;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
    }

    /// <summary>
    /// Extracts one region: <paramref name="blockCount"/> sectors (64, or fewer for the last
    /// region) starting at <paramref name="discOffset"/> (disc-relative). Returns the
    /// decrypted data (blockCount × 0x7C00) and the exceptions, whose offsets are relative to
    /// the region's hash area in the form block_index × 0x400 + offset_in_block (the writer
    /// converts them to chunk-relative offsets when splitting the region into chunks).
    /// </summary>
    public (byte[] Data, List<HashExceptionEntry> Exceptions) ExtractRegion(
        long discOffset, int blockCount, int blocksPerChunk)
    {
        try
        {
            return ExtractRegionCore(discOffset, blockCount, blocksPerChunk);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"ExtractRegion(0x{discOffset:X}, {blockCount}, {blocksPerChunk}): {e}", e);
        }
    }

    private (byte[] Data, List<HashExceptionEntry> Exceptions) ExtractRegionCore(
        long discOffset, int blockCount, int blocksPerChunk)
    {
        var encrypted = new byte[blockCount * PartitionRegionBuilder.SectorSize];
        var read = _input.ReadAt(discOffset, encrypted);
        if (read != encrypted.Length)
        {
            throw new RvzFormatException(
                $"Partition read at 0x{discOffset:X} returned {read} of {encrypted.Length} bytes.");
        }

        // Decrypt the data and hash areas of every sector.
        var data = new byte[blockCount * WiiHashCalculator.SectorDataSize];
        var hashBlocks = new byte[blockCount][];
        for (var block = 0; block < blockCount; block++)
        {
            var sector = encrypted.AsSpan(block * PartitionRegionBuilder.SectorSize);

            var dataBlock = new byte[WiiHashCalculator.SectorDataSize];
            var iv = sector.Slice(PartitionRegionBuilder.IvOffset, 16).ToArray();
            using (var decryptor = _aes.CreateDecryptor(_aes.Key, iv))
            {
                decryptor.TransformBlock(
                    sector.Slice(WiiHashCalculator.HashBlockSize, dataBlock.Length).ToArray(),
                    0, dataBlock.Length, dataBlock, 0);
            }

            dataBlock.CopyTo(data, block * WiiHashCalculator.SectorDataSize);

            var hashBlock = new byte[WiiHashCalculator.HashBlockSize];
            using (var hashDecryptor = _aes.CreateDecryptor(_aes.Key, new byte[16]))
            {
                hashDecryptor.TransformBlock(
                    sector[..WiiHashCalculator.HashBlockSize].ToArray(),
                    0, WiiHashCalculator.HashBlockSize, hashBlock, 0);
            }

            hashBlocks[block] = hashBlock;
        }

        // Recalculate the hash tree with the reader's zero-fill convention (h1 groups of 8,
        // h2 groups of 64; missing sectors hash as zero-filled h0 areas).
        var computed = ComputeHashBlocks(data, blockCount);

        // Compare every 20-byte hash; store an exception for each mismatch. The offsets are
        // region-relative in the form block_index × 0x400 + offset_in_block.
        var exceptions = new List<HashExceptionEntry>();
        for (var block = 0; block < blockCount; block++)
        {
            if (hashBlocks[block] == null || hashBlocks[block].Length != WiiHashCalculator.HashBlockSize ||
                computed[block] == null || computed[block].Length != WiiHashCalculator.HashBlockSize)
            {
                throw new InvalidOperationException(
                    $"hash block {block} invalid: got={hashBlocks[block]?.Length}/{computed[block]?.Length}");
            }

            CompareHashFields(hashBlocks[block], computed[block], block, exceptions);
        }

        return (data, exceptions);
    }

    /// <summary>
    /// Compares the six hash-block fields (h0, padding, h1, padding, h2, padding) in 20-byte
    /// windows, exactly like Dolphin's compare_hashes (WIABlob.cpp:1452-1455): the window of
    /// stride l starts at offset + min(l, size - 20), so fields not divisible by 20 (the
    /// 32-byte paddings) get overlapping windows (+0 and +12) instead of a partial trailing
    /// hash. Exceptions therefore always carry a full 20-byte hash.
    /// </summary>
    private static void CompareHashFields(byte[] desired, byte[] recalculated,
        int blockIndexInChunk, List<HashExceptionEntry> exceptions)
    {
        var fields = new (int Offset, int Size)[]
        {
            (0x000, 0x26C), // h0
            (0x26C, 0x14),  // padding_0
            (0x280, 0xA0),  // h1
            (0x320, 0x20),  // padding_1
            (0x340, 0xA0),  // h2
            (0x3E0, 0x20) // padding_2
        };

        foreach (var (fieldOffset, fieldSize) in fields)
        {
            for (var j = 0; j < fieldSize; j += WiiHashCalculator.HashSize)
            {
                var offset = fieldOffset + Math.Min(j, fieldSize - WiiHashCalculator.HashSize);
                var desiredSpan = desired.AsSpan(offset, WiiHashCalculator.HashSize);
                if (!desiredSpan.SequenceEqual(recalculated.AsSpan(offset, WiiHashCalculator.HashSize)))
                {
                    exceptions.Add(new HashExceptionEntry(
                        (ushort)(blockIndexInChunk * WiiHashCalculator.HashBlockSize + offset),
                        desiredSpan.ToArray()));
                }
            }
        }
    }

    /// <summary>
    /// Computes the 64 hash blocks of a region (zero-filling beyond <paramref name="blocks"/>)
    /// exactly like the reader's PartitionRegionBuilder: h1[j] = SHA1(h0 of sector 8g+j),
    /// h2[g] = SHA1(h1 array of group g).
    /// </summary>
    private static byte[][] ComputeHashBlocks(byte[] data, int blocks)
    {
        var h0Areas = new byte[64][];
        for (var sector = 0; sector < 64; sector++)
        {
            var h0 = new byte[WiiHashCalculator.HashBlockSize];
            if (sector < blocks)
            {
                WiiHashCalculator.BuildHashArea(
                    data.AsSpan(sector * WiiHashCalculator.SectorDataSize, WiiHashCalculator.SectorDataSize),
                    h0);
            }

            h0Areas[sector] = h0;
        }

        var h1Arrays = new byte[8][];
        var h2Array = new byte[8 * 20];
        for (var group = 0; group < 8; group++)
        {
            var h1 = new byte[8 * 20];
            for (var j = 0; j < 8; j++)
            {
                var sector = group * 8 + j;
                // Missing sectors: their data is zero-filled and hashed normally (Dolphin:
                // VolumeWii.cpp EncryptGroup; Go: part.go DevZero), so the h0 area is
                // 31 × SHA1(0x400 zeros) and h1 = SHA1 of that area — not SHA1 of a raw
                // zero buffer (that would miscompute the shared h1/h2 of the last region).
                var h0 = sector < blocks
                    ? h0Areas[sector].AsSpan(0, WiiHashCalculator.H0Size)
                    : WiiHashCalculator.ZeroSectorH0Area;
                SHA1.HashData(h0, h1.AsSpan(j * 20));
            }

            h1Arrays[group] = h1;
            SHA1.HashData(h1, h2Array.AsSpan(group * 20));
        }

        var result = new byte[64][];
        for (var sector = 0; sector < 64; sector++)
        {
            var hashBlock = new byte[WiiHashCalculator.HashBlockSize];
            h0Areas[sector].CopyTo(hashBlock, 0);
            h1Arrays[sector / 8].CopyTo(hashBlock, 0x280);
            h2Array.CopyTo(hashBlock, 0x340);
            result[sector] = hashBlock;
        }

        return result;
    }
}
