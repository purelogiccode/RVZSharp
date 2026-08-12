using RVZSharp.IO;

namespace RVZSharp.Format;

/// <summary>
/// <c>wia_part_t</c>: one Wii partition. Partition data is stored decrypted and without hashes;
/// the key allows re-encrypting it when reconstructing the original disc image.
/// </summary>
public readonly struct WiaPartEntry
{
    public const int Size = 0x30;

    /// <summary>The title key for this partition (128-bit AES).</summary>
    public byte[] Key { get; }

    /// <summary>The two data segments (segment 0 = management data, segment 1 = the rest).</summary>
    public WiaPartDataEntry[] Data { get; } // length 2

    public WiaPartEntry(byte[] key, WiaPartDataEntry[] data)
    {
        Key = key;
        Data = data;
    }

    /// <summary>
    /// Parses one entry from raw bytes of at least <see cref="Size"/>; any extra bytes
    /// (part_t_size &gt; 0x30) are ignored, matching Dolphin.
    /// </summary>
    public static WiaPartEntry Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new RvzFormatException(
                $"Partition entry needs at least {Size} bytes, only {data.Length} available.");
        }

        var reader = new SpanReader(data);
        var key = reader.ReadBytes(16).ToArray();
        var entries = new WiaPartDataEntry[2];
        for (var i = 0; i < entries.Length; i++)
        {
            var entryBytes = reader.ReadBytes(WiaPartDataEntry.Size);
            entries[i] = WiaPartDataEntry.Parse(entryBytes);
        }

        return new WiaPartEntry(key, entries);
    }
}
