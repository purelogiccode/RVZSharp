using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RVZSharp.Chunks;

namespace RVZSharp.Wii;

/// <summary>
/// Rebuilds the encrypted, hashed sectors of one 2 MiB region (64 sectors) of a Wii partition.
/// Partition data is stored in RVZ decrypted and without hashes; this builder recomputes the
/// h0/h1/h2 hash tree (zero-filling sectors beyond the partition end, like Dolphin), applies
/// the hash exceptions, and re-encrypts with the partition key (AES-128-CBC: zero IV for the
/// hash area, then the ciphertext at offset 0x3D0 as the IV for the data area), producing
/// byte-identical output to the original disc.
/// </summary>
public sealed class PartitionRegionBuilder
{
    public const int SectorsPerRegion = 64;
    public const int SectorSize = 0x8000;
    public const int IvOffset = 0x3D0;

    private readonly byte[] _key;
    private readonly byte[][] _h0Areas = new byte[SectorsPerRegion][];
    private readonly byte[][] _sectorData = new byte[SectorsPerRegion][];
    private readonly List<HashExceptionEntry>[] _exceptions = new List<HashExceptionEntry>[SectorsPerRegion];

    public PartitionRegionBuilder(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("A Wii partition key is 16 bytes.", nameof(key));
        }

        _key = key.ToArray();
        for (var i = 0; i < SectorsPerRegion; i++)
        {
            _exceptions[i] = [];
        }
    }

    /// <summary>Number of real sectors added so far (max <see cref="SectorsPerRegion"/>).</summary>
    public int SectorCount { get; private set; }

    /// <summary>
    /// Adds one sector of decoded partition data (0x7C00 bytes) together with any hash
    /// exceptions that apply to it. Exception offsets must be relative to the start of this
    /// 2 MiB region.
    /// </summary>
    public void AddSector(ReadOnlySpan<byte> data, ReadOnlySpan<HashExceptionEntry> exceptions)
    {
        if (SectorCount >= SectorsPerRegion)
        {
            throw new InvalidOperationException("This 2 MiB region is already full.");
        }

        if (data.Length != WiiHashCalculator.SectorDataSize)
        {
            throw new ArgumentException($"Sector data must be {WiiHashCalculator.SectorDataSize} bytes.");
        }

        var h0Area = new byte[WiiHashCalculator.HashBlockSize];
        WiiHashCalculator.BuildHashArea(data, h0Area);
        _h0Areas[SectorCount] = h0Area;
        _sectorData[SectorCount] = data.ToArray();
        _exceptions[SectorCount].AddRange(exceptions.ToArray());
        SectorCount++;
    }

    /// <summary>
    /// Produces the encrypted region sectors (<see cref="SectorCount"/> × 0x8000 bytes).
    /// Missing sectors are zero-filled for hash computation (Dolphin semantics).
    /// </summary>
    public byte[] Finish()
    {
        if (SectorCount == 0)
        {
            return [];
        }

        // H1: one SHA-1 of each sector's h0 area, grouped 8 per group (zero-filled beyond the
        // region's real sectors). H2: one SHA-1 of each group's h1 array, 8 groups per region.
        var h1Arrays = new byte[8][]; // [group][8 hashes]
        var h2Array = new byte[8 * 20];
        for (var group = 0; group < 8; group++)
        {
            var h1 = new byte[8 * 20];
            for (var j = 0; j < 8; j++)
            {
                var sector = group * 8 + j;
                // A sector beyond the partition data end: its data is zero-filled and hashed
                // normally (Dolphin: VolumeWii.cpp EncryptGroup; Go: part.go DevZero), so the
                // h0 area is 31 × SHA1(0x400 zeros) and h1 = SHA1 of that area — not SHA1 of
                // a raw zero buffer.
                var h0 = sector < _h0Areas.Length && _h0Areas[sector] != null
                    ? _h0Areas[sector].AsSpan(0, WiiHashCalculator.H0Size)
                    : WiiHashCalculator.ZeroSectorH0Area;
                var hash = new byte[20];
                SHA1.HashData(h0, hash);
                hash.CopyTo(h1, j * 20);
            }

            h1Arrays[group] = h1;

            var h2hash = new byte[20];
            SHA1.HashData(h1, h2hash);
            h2hash.CopyTo(h2Array, group * 20);
        }

        var output = new byte[SectorCount * SectorSize];
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var hashBlock = new byte[WiiHashCalculator.HashBlockSize];
        var encryptedHash = new byte[WiiHashCalculator.HashBlockSize];

        for (var sector = 0; sector < SectorCount; sector++)
        {
            // Assemble the hash block: h0 + padding + h1 array + padding + h2 array + padding.
            _h0Areas[sector].CopyTo(hashBlock, 0);
            h1Arrays[sector / 8].CopyTo(hashBlock, 0x280);
            h2Array.CopyTo(hashBlock, 0x340);

            // Apply the hash exceptions (they replace hashes before encryption).
            WiiHashCalculator.ApplyHashExceptions(CollectionsMarshal.AsSpan(_exceptions[sector]), hashBlock);

            var sectorOut = output.AsSpan(sector * SectorSize);

            aes.IV = new byte[16];
            using (var encryptor = aes.CreateEncryptor())
            {
                encryptor.TransformBlock(hashBlock, 0, hashBlock.Length, encryptedHash, 0);
            }

            encryptedHash.CopyTo(sectorOut);

            var data = _sectorData[sector];
            aes.IV = encryptedHash.AsSpan(IvOffset, 16).ToArray();
            using (var encryptor = aes.CreateEncryptor())
            {
                var dataOut = new byte[WiiHashCalculator.SectorDataSize];
                encryptor.TransformBlock(data, 0, data.Length, dataOut, 0);
                dataOut.CopyTo(sectorOut.Slice(WiiHashCalculator.HashBlockSize));
            }
        }

        return output;
    }
}
