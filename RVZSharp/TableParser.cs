using System.Security.Cryptography;
using RVZSharp.Compression;
using RVZSharp.Models;
using RVZSharp.IO;

namespace RVZSharp;

/// <summary>
/// Reads the three data tables of an RVZ file. The partition table is stored uncompressed;
/// the raw-data and group tables are compressed with the disc's compression method.
/// </summary>
public static class TableParser
{
    /// <summary>Reads and validates the Wii partition table (uncompressed, at part_off).</summary>
    public static WiaPartEntry[] ParsePartitions(Stream file, WiaDisc disc)
    {
        var count = disc.NumPartitions;
        if (count == 0)
        {
            // Dolphin verifies the partition-table hash even when the table is empty
            // (WIABlob.cpp:165-175); the writers store SHA-1 of the empty byte buffer.
            var emptyHash = SHA1.HashData(ReadOnlySpan<byte>.Empty);
            if (!emptyHash.AsSpan().SequenceEqual(disc.PartitionEntriesHash))
            {
                throw new RvzHashMismatchException("The partition table SHA-1 does not match its contents.");
            }

            return [];
        }

        var tableSize = checked((long)count * disc.PartitionEntrySize);
        using var section = new SectionStream(file, (long)disc.PartitionEntriesOffset, tableSize);
        var raw = new byte[tableSize];
        section.ReadExactly(raw);

        var actualHash = SHA1.HashData(raw);
        if (!actualHash.AsSpan().SequenceEqual(disc.PartitionEntriesHash))
        {
            throw new RvzHashMismatchException("The partition table SHA-1 does not match its contents.");
        }

        // Dolphin accepts entries smaller than 0x30 and zero-fills the remainder
        // (WIABlob.cpp:177-185: copy_length = min(entry_size, sizeof(PartitionEntry)));
        // entries larger than 0x30 are truncated (extra bytes ignored).
        var copyLength = (int)Math.Min(disc.PartitionEntrySize, WiaPartEntry.Size);
        var entries = new WiaPartEntry[count];
        for (var i = 0; i < count; i++)
        {
            var entryBytes = raw.AsSpan((int)(i * disc.PartitionEntrySize), copyLength);
            if (copyLength < WiaPartEntry.Size)
            {
                var padded = new byte[WiaPartEntry.Size];
                entryBytes.CopyTo(padded);
                entryBytes = padded;
            }

            entries[i] = WiaPartEntry.Parse(entryBytes);
        }

        return entries;
    }

    /// <summary>
    /// Reads the raw-data table (compressed with the disc's method at raw_data_off) and aligns
    /// every entry to a 32 KiB sector boundary (the first raw entry starts at 0x80/0x4FF80 but
    /// covers the full 0x50000 first sector).
    /// </summary>
    public static WiaRawDataEntry[] ParseRawDataEntries(Stream file, WiaDisc disc)
    {
        var count = disc.NumRawDataEntries;
        if (count == 0)
        {
            return [];
        }

        var tableSize = checked((long)count * WiaRawDataEntry.Size);
        var bytes = ReadCompressedTable(file, disc, (long)disc.RawDataEntriesOffset,
            disc.RawDataEntriesSize, tableSize, "raw-data");

        var entries = new WiaRawDataEntry[count];
        for (var i = 0; i < count; i++)
        {
            var entry = WiaRawDataEntry.Parse(bytes.AsSpan(i * WiaRawDataEntry.Size, WiaRawDataEntry.Size));

            // Round the offset down to the previous 32 KiB boundary and grow the size so the
            // end offset stays the same (handles the first raw entry at 0x80 without special casing).
            var remainder = entry.RawDataOffset % WiaDisc.SectorSize;
            entries[i] = new WiaRawDataEntry(
                entry.RawDataOffset - remainder,
                entry.RawDataSize + remainder,
                entry.GroupIndex,
                entry.NumGroups);
        }

        return entries;
    }

    /// <summary>Reads the group table (compressed with the disc's method at group_off).</summary>
    public static GroupEntry[] ParseGroupEntries(Stream file, WiaDisc disc)
    {
        return ParseGroupEntries(file, disc, WiaRvzFormat.Rvz);
    }

    /// <summary>Reads the group table for WIA (8-byte entries) or RVZ (12-byte entries).</summary>
    public static GroupEntry[] ParseGroupEntries(Stream file, WiaDisc disc, WiaRvzFormat format)
    {
        var count = disc.NumGroups;
        if (count == 0)
        {
            return [];
        }

        var entrySize = format == WiaRvzFormat.Wia ? WiaGroupEntry.Size : RvzGroupEntry.Size;
        var tableSize = checked(count * entrySize);
        var bytes = ReadCompressedTable(file, disc, (long)disc.GroupEntriesOffset,
            disc.GroupEntriesSize, tableSize, "group");

        var entries = new GroupEntry[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = format == WiaRvzFormat.Wia
                ? GroupEntry.FromWia(WiaGroupEntry.Parse(bytes.AsSpan(i * entrySize, entrySize)))
                : GroupEntry.FromRvz(RvzGroupEntry.Parse(bytes.AsSpan(i * entrySize, entrySize)));
        }

        return entries;
    }

    private static byte[] ReadCompressedTable(Stream file, WiaDisc disc, long offset,
        uint compressedSize, long expectedSize, string name)
    {
        using var section = new SectionStream(file, offset, compressedSize);

        // PURGE is not a streaming codec; the table is a single PURGE stream with a SHA-1 trailer.
        if (disc.Compression == CompressionType.Purge)
        {
            return PurgeDecoder.Decode(ReadAll(section), [], (int)expectedSize);
        }

        var decoder = CompressionCodecFactory.Create(disc.Compression);
        using var decompressor = decoder.CreateDecompressor(
            section, disc.ComprData.AsSpan(0, disc.ComprDataLen), compressedSize, expectedSize);

        var output = new byte[expectedSize];
        var total = 0;
        try
        {
            while (total < expectedSize)
            {
                var read = decompressor.Read(output, total, (int)Math.Min(8192, expectedSize - total));
                if (read <= 0)
                {
                    throw new RvzFormatException(
                        $"The {name} table decompressed to {total} bytes, expected {expectedSize}.");
                }

                total += read;
            }
        }
        catch (RvzException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ZstdSharp.ZstdException
                                      or ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            throw new RvzFormatException($"Failed to decompress the {name} table: {e.Message}", e);
        }

        // Dolphin rejects decompressed output larger than the declared size
        // (WIABlob.cpp:741-754): the decompressor must be at end-of-stream right after the
        // expected bytes. A stream that produced extra output fails this probe read.
        if (decompressor.ReadByte() != -1)
        {
            throw new RvzFormatException(
                $"The {name} table decompressed to more than {expectedSize} bytes.");
        }

        return output;
    }

    private static byte[] ReadAll(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
