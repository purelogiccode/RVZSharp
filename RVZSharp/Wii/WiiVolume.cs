using RVZSharp.Interfaces;

namespace RVZSharp.Wii;

/// <summary>One Wii disc partition found via the disc's partition table.</summary>
public readonly struct Partition
{
    public required ulong Offset { get; init; }
    public required uint Type { get; init; }

    /// <summary>data_offset (shifted) from the partition header: bytes from the partition start.</summary>
    public required ulong DataOffset { get; init; }

    /// <summary>data_size (shifted) from the partition header, in bytes.</summary>
    public required ulong DataSize { get; init; }

    /// <summary>The 16-byte title key from the partition's ticket.</summary>
    public required byte[] Key { get; init; }
}

/// <summary>
/// Wii disc volume helpers for the writer (Dolphin: VolumeWii): partition table discovery,
/// ticket parsing and the "shifted" big-endian reads of the partition header.
/// </summary>
public static class WiiVolume
{
    public const ulong DiscHeaderSize = 0x80;
    public const ulong PartitionTableAddress = 0x40000;
    public const ulong PartitionHeaderSize = 0x400;
    public const uint WII_MAGIC = 0x5D1C9EA3;
    public const uint GC_MAGIC = 0xC2339F3D;
    public const uint PARTITION_NONE = 0xFFFFFFFF;

    /// <summary>True for a Wii disc whose partition data has hash trees (disc header 0x60).</summary>
    public static bool HasWiiHashes(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return header[0x60] == 0;
    }

    /// <summary>True for a Wii disc whose partition data is encrypted (disc header 0x61).</summary>
    public static bool HasWiiEncryption(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return header[0x61] == 0;
    }

    /// <summary>Reads the disc type from the DVD/Wii magic in the disc header.</summary>
    public static bool IsWiiDisc(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return ReadBe32(header, 0x18) == WII_MAGIC;
    }

    /// <summary>
    /// Returns the partitions of a Wii disc, read from the four partition table groups at
    /// 0x40000 (Dolphin: VolumeWii::GetPartitions). Only partitions with a valid ticket and
    /// plausible data ranges are returned.
    /// </summary>
    public static List<Partition> GetPartitions(IBlobReader disc)
    {
        var partitions = new List<Partition>();
        var header = new byte[0x80];
        disc.ReadAt(0, header);

        Span<byte> tableInfo = stackalloc byte[8];
        Span<byte> entry = stackalloc byte[8];
        // The full ticket (Dolphin: sizeof(IOS::ES::Ticket) = 0x2A4), so the validation
        // covers the whole structure like TicketReader::IsValid (Formats.cpp:368-377):
        // signature type + a complete ticket buffer (the key sits at 0x1BF).
        Span<byte> ticket = stackalloc byte[0x2A4];
        for (var group = 0; group < 4; group++)
        {
            disc.ReadAt((long)(PartitionTableAddress + (ulong)group * 8), tableInfo);
            var count = ReadBe32(tableInfo, 0);
            var tableOffset = (ulong)ReadBe32(tableInfo, 4) << 2;

            for (var i = 0; i < count; i++)
            {
                if (!TryReadAt(disc, tableOffset + (ulong)i * 8, entry))
                {
                    break;
                }

                var partitionOffset = (ulong)ReadBe32(entry, 0) << 2;
                var partitionType = ReadBe32(entry, 4);
                if (partitionOffset == 0 || partitionOffset >= (ulong)disc.Length)
                {
                    continue;
                }

                // The partition header starts with the ticket; require a valid RSA2048
                // ticket so we can decrypt the partition data (Dolphin: TicketReader::IsValid).
                if (!TryReadAt(disc, partitionOffset, ticket))
                {
                    continue;
                }

                if (ReadBe32(ticket, 0) != 0x10001)
                {
                    continue;
                }

                var dataOffset = ReadSwappedAndShifted(disc, partitionOffset + 0x2B8);
                var dataSize = ReadSwappedAndShifted(disc, partitionOffset + 0x2BC);
                if (dataOffset == null || dataSize == null)
                {
                    continue;
                }

                var key = ticket.Slice(0x1BF, 16).ToArray();
                partitions.Add(new Partition
                {
                    Offset = partitionOffset,
                    Type = partitionType,
                    DataOffset = dataOffset.Value,
                    DataSize = dataSize.Value,
                    Key = key
                });
            }
        }

        // Dolphin sorts partitions and drops duplicates/overlaps.
        partitions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var result = new List<Partition>();
        foreach (var partition in partitions)
        {
            if (result.Count > 0 && result[^1].Offset == partition.Offset)
            {
                continue;
            }

            result.Add(partition);
        }

        return result;
    }

    /// <summary>The FST offset within the partition (partition header 0x424, shifted).</summary>
    public static ulong? GetFstOffset(IBlobReader disc, Partition partition)
    {
        return ReadSwappedAndShifted(disc, partition.Offset + 0x424);
    }

    /// <summary>The FST size (partition header 0x428, shifted).</summary>
    public static ulong? GetFstSize(IBlobReader disc, Partition partition)
    {
        return ReadSwappedAndShifted(disc, partition.Offset + 0x428);
    }

    /// <summary>Maps a partition-data-relative offset to a disc-relative offset.</summary>
    public static ulong PartitionOffsetToRawOffset(ulong offset, Partition partition)
    {
        return partition.Offset + partition.DataOffset + offset;
    }

    /// <summary>Reads a big-endian u32 at <paramref name="offset"/>.</summary>
    public static uint ReadSwapped(IBlobReader disc, ulong offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        return TryReadAt(disc, offset, bytes) ? ReadBe32(bytes, 0) : 0;
    }

    /// <summary>Reads a big-endian u32 and shifts it left by 2 (Dolphin: ReadSwappedAndShifted).</summary>
    public static ulong? ReadSwappedAndShifted(IBlobReader disc, ulong offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        return TryReadAt(disc, offset, bytes) ? (ulong)ReadBe32(bytes, 0) << 2 : null;
    }

    private static bool TryReadAt(IBlobReader disc, ulong offset, Span<byte> buffer)
    {
        return offset < (ulong)disc.Length && disc.ReadAt((long)offset, buffer) == buffer.Length;
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
