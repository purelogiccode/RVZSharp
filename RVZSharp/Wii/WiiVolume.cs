using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Wii;

/// <summary>
/// Wii disc volume helpers for the writer (Dolphin: VolumeWii): partition table discovery,
/// ticket parsing and the "shifted" big-endian reads of the partition header.
/// </summary>
public static class WiiVolume
{
    /// <summary>Size of the disc header (0x80 bytes).</summary>
    public const ulong DiscHeaderSize = 0x80;

    /// <summary>Offset of the partition table on disc (0x40000).</summary>
    public const ulong PartitionTableAddress = 0x40000;

    /// <summary>Size of a partition header, including its ticket (0x400 bytes).</summary>
    public const ulong PartitionHeaderSize = 0x400;

    /// <summary>Wii disc magic number.</summary>
    public const uint WII_MAGIC = 0x5D1C9EA3;

    /// <summary>GameCube disc magic number.</summary>
    public const uint GC_MAGIC = 0xC2339F3D;

    /// <summary>Partition table entry value meaning "no partition".</summary>
    public const uint PARTITION_NONE = 0xFFFFFFFF;

    /// <summary>True for a Wii disc whose partition data has hash trees (disc header 0x60).</summary>
    /// <param name="disc">The disc image to inspect.</param>
    /// <returns>True when the disc stores hash trees.</returns>
    public static bool HasWiiHashes(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return header[0x60] == 0;
    }

    /// <summary>True for a Wii disc whose partition data is encrypted (disc header 0x61).</summary>
    /// <param name="disc">The disc image to inspect.</param>
    /// <returns>True when the disc stores encrypted partitions.</returns>
    public static bool HasWiiEncryption(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return header[0x61] == 0;
    }

    /// <summary>Reads the disc type from the DVD/Wii magic in the disc header.</summary>
    /// <param name="disc">The disc image to inspect.</param>
    /// <returns>True when the disc magic is the Wii magic.</returns>
    public static bool IsWiiDisc(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x80];
        disc.ReadAt(0, header);
        return ReadBe32(header, 0x18) == WII_MAGIC;
    }

    /// <summary>
    /// Detects the disc type from the header magic (Dolphin: TryCreateDisc, Volume.cpp): the
    /// Wii magic lives at 0x18 and the GameCube magic at 0x1C of the 0x80-byte disc header.
    /// </summary>
    /// <param name="disc">The disc image to inspect.</param>
    /// <returns><see cref="DiscType.Wii"/>, <see cref="DiscType.GameCube"/> or
    /// <see cref="DiscType.Unknown"/> when the image carries neither magic.</returns>
    public static DiscType GetDiscType(IBlobReader disc)
    {
        Span<byte> header = stackalloc byte[0x20];
        if (!TryReadAt(disc, 0, header))
        {
            return DiscType.Unknown;
        }

        if (ReadBe32(header, 0x18) == WII_MAGIC)
        {
            return DiscType.Wii;
        }

        if (ReadBe32(header, 0x1C) == GC_MAGIC)
        {
            return DiscType.GameCube;
        }

        return DiscType.Unknown;
    }

    /// <summary>
    /// Returns the partitions of a Wii disc, read from the four partition table groups at
    /// 0x40000 (Dolphin: VolumeWii::GetPartitions). Only partitions with a valid ticket and
    /// plausible data ranges are returned.
    /// </summary>
    /// <param name="disc">The disc image to inspect.</param>
    /// <returns>The sorted, de-duplicated list of valid partitions.</returns>
    public static IReadOnlyList<Partition> GetPartitions(IBlobReader disc)
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
    /// <param name="disc">The disc image.</param>
    /// <param name="partition">The partition whose FST offset is requested.</param>
    /// <returns>The FST offset, or null when it cannot be read.</returns>
    public static ulong? GetFstOffset(IBlobReader disc, Partition partition)
    {
        return ReadSwappedAndShifted(disc, partition.Offset + 0x424);
    }

    /// <summary>The FST size (partition header 0x428, shifted).</summary>
    /// <param name="disc">The disc image.</param>
    /// <param name="partition">The partition whose FST size is requested.</param>
    /// <returns>The FST size, or null when it cannot be read.</returns>
    public static ulong? GetFstSize(IBlobReader disc, Partition partition)
    {
        return ReadSwappedAndShifted(disc, partition.Offset + 0x428);
    }

    /// <summary>Maps a partition-data-relative offset to a disc-relative offset.</summary>
    /// <param name="offset">The offset inside the partition data area.</param>
    /// <param name="partition">The partition holding the offset.</param>
    /// <returns>The corresponding raw disc offset.</returns>
    public static ulong PartitionOffsetToRawOffset(ulong offset, Partition partition)
    {
        return partition.Offset + partition.DataOffset + offset;
    }

    /// <summary>Reads a big-endian u32 at <paramref name="offset"/>.</summary>
    /// <param name="disc">The disc image.</param>
    /// <param name="offset">The disc offset to read.</param>
    /// <returns>The value read, or 0 when the read fails.</returns>
    public static uint ReadSwapped(IBlobReader disc, ulong offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        return TryReadAt(disc, offset, bytes) ? ReadBe32(bytes, 0) : 0;
    }

    /// <summary>Reads a big-endian u32 and shifts it left by 2 (Dolphin: ReadSwappedAndShifted).</summary>
    /// <param name="disc">The disc image.</param>
    /// <param name="offset">The byte offset to read.</param>
    /// <returns>The shifted value, or null when the read fails.</returns>
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
