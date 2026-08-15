using RVZSharp.Wii;

namespace RVZSharp.Tests.Helpers;

/// <summary>
/// Builds a realistic synthetic Wii ISO that RvzWriter's partition detection accepts: a disc
/// header with Wii magic + hash/encryption flags, a partition table at 0x40000, a partition
/// with a valid RSA2048 ticket (title key at 0x1BF), a data_offset/data_size header, and
/// encrypted partition data produced by PartitionRegionBuilder.
/// </summary>
public static class TestWiiIsoBuilder
{
    public const int PartitionOffset = 0x100000;
    public const int DataOffset = 0x40000; // partition data starts at PartitionOffset + DataOffset

    /// <summary>
    /// Builds the ISO. When <paramref name="corruptSomeHashes"/> is set, the encrypted hash
    /// areas of a few sectors are flipped so the writer must detect hash exceptions.
    /// </summary>
    public static byte[] Build(byte[] key, int sectorCount, byte[] decryptedData,
        bool corruptSomeHashes = false)
    {
        var dataStart = PartitionOffset + DataOffset;
        var dataSize = sectorCount * 0x8000;
        var isoSize = dataStart + dataSize + 0x120000;
        var iso = new byte[isoSize];
        new Random(12345).NextBytes(iso);

        // Disc header: Wii magic at 0x18, partition table info at 0x1C, hash/encryption flags.
        WriteBe32(iso, 0x18, 0x5D1C9EA3);
        WriteBe32(iso, 0x1C, 1); // one partition table entry
        iso[0x60] = 0; // hashes present
        iso[0x61] = 0; // encrypted

        // Partition table at 0x40000: { offset << 2, type }.
        WriteBe32(iso, 0x40000, PartitionOffset >> 2);
        WriteBe32(iso, 0x40004, 0);

        // Partition header at PartitionOffset: ticket + data offset/size + FST fields.
        WriteBe32(iso, PartitionOffset, 0x10001u); // RSA2048 signature type
        key.CopyTo(iso, PartitionOffset + 0x1BF);
        WriteBe32(iso, PartitionOffset + 0x2B8, DataOffset >> 2);
        WriteBe32(iso, PartitionOffset + 0x2BC, (uint)(dataSize >> 2));
        WriteBe32(iso, PartitionOffset + 0x424, 0); // FST offset
        WriteBe32(iso, PartitionOffset + 0x428, 0); // FST size

        // Encrypt the partition data (region by region, like TestRvzBuilder does).
        using (var output = new MemoryStream())
        {
            for (var regionStart = 0; regionStart < sectorCount; regionStart += 64)
            {
                var regionEnd = Math.Min(regionStart + 64, sectorCount);
                var builder = new PartitionRegionBuilder(key);
                for (var sector = regionStart; sector < regionEnd; sector++)
                {
                    builder.AddSector(decryptedData.AsSpan(sector * 0x7C00, 0x7C00), []);
                }

                output.Write(builder.Finish());
            }

            output.Position = 0;
            output.Read(iso, dataStart, (int)output.Length);
        }

        if (corruptSomeHashes)
        {
            // Flip a few hash bytes inside the encrypted data of sectors 5 and 70: the writer
            // must detect the mismatch and store hash exceptions.
            foreach (var sector in new[] { 5, 70 })
            {
                var blockOffset = dataStart + sector * 0x8000;
                for (var i = 0; i < 20; i++)
                {
                    iso[blockOffset + i] ^= 0xFF;
                }
            }
        }

        return iso;
    }

    /// <summary>Generates sectorCount × 0x7C00 bytes of random decrypted data.</summary>
    public static byte[] RandomData(int sectorCount, int seed = 7)
    {
        var data = new byte[sectorCount * 0x7C00];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
