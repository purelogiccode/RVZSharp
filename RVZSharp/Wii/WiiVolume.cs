using System.Security.Cryptography;
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

    // Console common keys (Dolphin: IOSC.cpp). A ticket stores its title key AES-CBC
    // encrypted with the console's common key, using the ticket's title ID as the IV.
    private static readonly byte[] RetailCommonKey =
    [
        0xEB, 0xE4, 0x2A, 0x22, 0x5E, 0x85, 0x93, 0xE4,
        0x48, 0xD9, 0xC5, 0x45, 0x73, 0x81, 0xAA, 0xF7
    ];

    private static readonly byte[] KoreanCommonKey =
    [
        0x63, 0xB8, 0x2B, 0xB4, 0xF4, 0x61, 0x4E, 0x2E,
        0x13, 0xF2, 0xFE, 0xFB, 0xBA, 0x4C, 0x9B, 0x7E
    ];

    private static readonly byte[] RvtCommonKey =
    [
        0xA1, 0x60, 0x4A, 0x6A, 0x71, 0x23, 0xB5, 0x29,
        0xAE, 0x8B, 0xEC, 0x32, 0xC8, 0x16, 0xFC, 0xAA
    ];

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
        var containerKeys = GetContainerPartitionKeys(disc);

        Span<byte> tableInfo = stackalloc byte[8];
        Span<byte> entry = stackalloc byte[8];
        // The full ticket (Dolphin: sizeof(IOS::ES::Ticket) = 0x2A4), so the validation
        // covers the whole structure like TicketReader::IsValid (Formats.cpp:368-377):
        // signature type + a complete ticket buffer (the key sits at 0x1BF).
        Span<byte> ticket = stackalloc byte[0x2A4];
        // A real Wii disc has a handful of partition entries; a hostile table count would
        // otherwise drive a multi-billion-iteration read loop. Unused groups often carry
        // garbage (their table offset points outside the file), so only a huge count with a
        // readable table is rejected — a bad offset is skipped like Dolphin.
        const uint maxEntriesPerGroup = 4096;
        for (var group = 0; group < 4; group++)
        {
            disc.ReadAt((long)(PartitionTableAddress + (ulong)group * 8), tableInfo);
            var count = ReadBe32(tableInfo, 0);
            var tableOffset = (ulong)ReadBe32(tableInfo, 4) << 2;
            if (count > maxEntriesPerGroup)
            {
                if (tableOffset >= (ulong)disc.Length)
                {
                    continue;
                }

                throw new RvzFormatException(
                    $"The partition table group {group} declares {count} entries.");
            }


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

                // The container (RVZ/WIA) stores the authoritative partition key; the ticket on
                // the decoded disc can carry a re-signed (different) title key, so container
                // keys win (Dolphin: WIABlob partition keys). Plain ISOs fall back to the
                // ticket title key, which is common-key encrypted on retail discs.
                var key = containerKeys.TryGetValue(
                    partitionOffset + dataOffset.Value, out var containerKey)
                    ? containerKey
                    : GetTitleKey(ticket);
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

    /// <summary>
    /// Decrypts the title key stored in a partition ticket (Dolphin: TicketReader::GetTitleKey).
    /// Retail tickets store the 16 bytes at 0x1BF AES-CBC encrypted with the console's common
    /// key, using the ticket's title ID at 0x1DC as the IV. The common key is chosen by the
    /// ticket issuer (RVT/iQue discs) or the common-key index at 0x1F1 (0 = retail, 1 = Korean).
    /// </summary>
    /// <param name="ticket">A complete 0x2A4-byte RSA2048 ticket, including its signature.</param>
    /// <returns>The decrypted 16-byte title key.</returns>
    /// <exception cref="ArgumentException"><paramref name="ticket"/> is shorter than a ticket.</exception>
    public static byte[] GetTitleKey(ReadOnlySpan<byte> ticket)
    {
        if (ticket.Length < 0x1F2)
        {
            throw new ArgumentException(
                "The ticket is too short to contain a title key.", nameof(ticket));
        }

        Span<byte> iv = stackalloc byte[16];
        ticket.Slice(0x1DC, 8).CopyTo(iv);
        var encrypted = ticket.Slice(0x1BF, 16).ToArray();
        var key = new byte[16];
        using var aes = Aes.Create();
        aes.Key = SelectCommonKey(ticket);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aes.Key, iv.ToArray());
        decryptor.TransformBlock(encrypted, 0, encrypted.Length, key, 0);
        return key;
    }

    private static byte[] SelectCommonKey(ReadOnlySpan<byte> ticket)
    {
        // RVT (iQue) tickets are issued by Root-CA00000002-XS00000006 (Dolphin: GetConsoleType);
        // the issuer string lives in the signature structure at 0x140.
        if (ticket.Length >= 0x180 &&
            ticket.Slice(0x140, 0x40).StartsWith("Root-CA00000002-XS00000006"u8))
        {
            return RvtCommonKey;
        }

        // Any index other than 1 falls back to the retail key, like Dolphin.
        return ticket[0x1F1] == 1 ? KoreanCommonKey : RetailCommonKey;
    }

    /// <summary>
    /// Returns the authoritative partition keys of an RVZ/WIA container, keyed by the raw disc
    /// offset where each partition's data starts (Dolphin: WIABlob partition entries).
    /// </summary>
    private static Dictionary<ulong, byte[]> GetContainerPartitionKeys(IBlobReader disc)
    {
        var keys = new Dictionary<ulong, byte[]>();
        if (disc is not RvzReader container)
        {
            return keys;
        }

        foreach (var partition in container.Partitions)
        {
            foreach (var segment in partition.Data)
            {
                if (segment.NumSectors != 0)
                {
                    keys[(ulong)segment.FirstSector * WiaDisc.SectorSize] = partition.Key;
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Detects the boot-header offset shift of a disc view (Dolphin: Volume::GetOffsetShift):
    /// Wii partitions store offsets in 4-byte units (shift 2), GameCube discs in bytes (shift 0).
    /// </summary>
    /// <param name="view">A decrypted Wii partition view or a GameCube disc.</param>
    /// <returns>The shift (0 or 2), or null when the view has no GameCube/Wii magic.</returns>
    public static int? TryGetOffsetShift(IBlobReader view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Span<byte> magic = stackalloc byte[4];
        if (view.ReadAt(0x18, magic) == 4 &&
            ReadBe32(magic, 0) == WII_MAGIC)
        {
            return 2;
        }

        if (view.ReadAt(0x1C, magic) == 4 &&
            ReadBe32(magic, 0) == GC_MAGIC)
        {
            return 0;
        }

        return null;
    }

    /// <summary>The FST offset within the partition (partition header 0x424, shifted).</summary>
    /// <param name="disc">The disc image.</param>
    /// <param name="partition">The partition whose FST offset is requested.</param>
    /// <returns>The FST offset, or null when it cannot be read.</returns>
    public static ulong? GetFstOffset(IBlobReader disc, Partition partition)
    {
        using var view = new PartitionReader(disc, partition);
        return ReadSwappedAndShifted(view, 0x424);
    }

    /// <summary>The FST size (partition header 0x428, shifted).</summary>
    /// <param name="disc">The disc image.</param>
    /// <param name="partition">The partition whose FST size is requested.</param>
    /// <returns>The FST size, or null when it cannot be read.</returns>
    public static ulong? GetFstSize(IBlobReader disc, Partition partition)
    {
        using var view = new PartitionReader(disc, partition);
        return ReadSwappedAndShifted(view, 0x428);
    }

    /// <summary>
    /// The apploader size for a decrypted partition view (Dolphin: GetApploaderSize):
    /// 0x20-byte header plus the size and trailer size stored at 0x2454/0x2458.
    /// </summary>
    /// <param name="view">A decrypted partition view (or a GameCube disc).</param>
    /// <returns>The apploader size, or null when it cannot be read.</returns>
    public static ulong? GetApploaderSize(IBlobReader view)
    {
        if (!TryReadSwapped(view, 0x2440 + 0x14, out var size) ||
            !TryReadSwapped(view, 0x2440 + 0x18, out var trailer))
        {
            return null;
        }

        return 0x20UL + size + trailer;
    }

    /// <summary>
    /// The boot DOL offset for a decrypted partition view (Dolphin: GetBootDOLOffset); the
    /// Datel AR disc stores 0 and does not use a DOL.
    /// </summary>
    /// <param name="view">A decrypted partition view (or a GameCube disc).</param>
    /// <returns>The DOL offset, or null when it is absent or zero.</returns>
    public static ulong? GetBootDolOffset(IBlobReader view)
    {
        if (!TryReadSwapped(view, 0x420, out var value) ||
            TryGetOffsetShift(view) is not { } shift)
        {
            return null;
        }

        var offset = (ulong)value << shift;
        return offset == 0 ? null : offset;
    }

    /// <summary>
    /// The boot DOL size for a decrypted partition view (Dolphin: GetBootDOLSize): the largest
    /// end offset of the seven text and eleven data segments in the DOL header.
    /// </summary>
    /// <param name="view">A decrypted partition view (or a GameCube disc).</param>
    /// <param name="dolOffset">The DOL offset, from <see cref="GetBootDolOffset"/>.</param>
    /// <returns>The DOL size, or null when the header cannot be read.</returns>
    public static uint? GetBootDolSize(IBlobReader view, ulong dolOffset)
    {
        uint size = 0;
        for (var i = 0; i < 7; i++)
        {
            if (!TryReadSwapped(view, dolOffset + (ulong)(0x00 + i * 4), out var offset) ||
                !TryReadSwapped(view, dolOffset + (ulong)(0x90 + i * 4), out var segmentSize))
            {
                return null;
            }

            size = Math.Max(size, offset + segmentSize);
        }

        for (var i = 0; i < 11; i++)
        {
            if (!TryReadSwapped(view, dolOffset + (ulong)(0x1C + i * 4), out var offset) ||
                !TryReadSwapped(view, dolOffset + (ulong)(0xAC + i * 4), out var segmentSize))
            {
                return null;
            }

            size = Math.Max(size, offset + segmentSize);
        }

        return size;
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

    private static bool TryReadSwapped(IBlobReader disc, ulong offset, out uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (TryReadAt(disc, offset, bytes))
        {
            value = ReadBe32(bytes, 0);
            return true;
        }

        value = 0;
        return false;
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
