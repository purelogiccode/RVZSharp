using RVZSharp.Chunks;
using RVZSharp.Models;

namespace RVZSharp.Tests;

public class ExceptionListParserTests
{
    private static HashExceptionEntry Entry(ushort offset, byte firstByte = 0xAB)
    {
        var hash = Enumerable.Range(0, 20).Select(i => (byte)(firstByte + i)).ToArray();
        return new HashExceptionEntry(offset, hash);
    }

    /// <summary>HashExceptionEntry is a plain struct whose byte[] fields compare by reference;
    /// equality must be checked field by field.</summary>
    private static void AssertEntriesEqual(HashExceptionEntry[] expected, HashExceptionEntry[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Offset, actual[i].Offset);
            Assert.Equal(expected[i].Hash, actual[i].Hash);
        }
    }

    private static void AssertListsEqual(HashExceptionEntry[][] expected, HashExceptionEntry[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            AssertEntriesEqual(expected[i], actual[i]);
        }
    }

    private static byte[] Serialize(params HashExceptionEntry[][] lists)
    {
        using var output = new MemoryStream();
        foreach (var list in lists)
        {
            WriteBe16(output, (ushort)list.Length);
            foreach (var entry in list)
            {
                WriteBe16(output, entry.Offset);
                output.Write(entry.Hash);
            }
        }

        return output.ToArray();
    }

    private static void WriteBe16(Stream output, ushort value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    [Fact]
    public void ZeroLists_ReturnsEmpty()
    {
        var (lists, bytesUsed) = ExceptionListParser.Parse([], 0, alignTo4: false);

        Assert.Empty(lists);
        Assert.Equal(0, bytesUsed);
    }

    [Fact]
    public void ZeroCountList_ConsumesOnlyTheCount()
    {
        var (lists, bytesUsed) = ExceptionListParser.Parse("\0\0"u8, 1, alignTo4: false);

        Assert.Single(lists);
        Assert.Empty(lists[0]);
        Assert.Equal(2, bytesUsed);
    }

    [Fact]
    public void SingleList_ParsesAllEntries()
    {
        var expected = new[] { Entry(0x0000), Entry(0x0014, 0x10), Entry(0x0100, 0x77) };
        var data = Serialize(expected);

        var (lists, bytesUsed) = ExceptionListParser.Parse(data, 1, alignTo4: false);

        Assert.Single(lists);
        AssertEntriesEqual(expected, lists[0]);
        Assert.Equal(2 + 3 * HashExceptionEntry.Size, bytesUsed);
    }

    [Fact]
    public void SingleList_AlignedTo4_PadsTheListEnd()
    {
        // The padding bytes are part of the parsed data, so they must be present in the input
        // and the parser consumes them.
        var entries = new[] { Entry(0x0000), Entry(0x0014, 0x10) };
        var data = Serialize(entries).Concat(new byte[2]).ToArray(); // 46 bytes + 2 of padding
        Assert.Equal(48, data.Length);

        var (lists, bytesUsed) = ExceptionListParser.Parse(data, 1, alignTo4: true);

        AssertEntriesEqual(entries, lists[0]);
        Assert.Equal(48, bytesUsed); // padded to the 4-byte boundary
    }

    [Fact]
    public void AlignedList_WithoutPadding_Bytes_Throws()
    {
        // One list of two entries (46 bytes): aligned parsing needs the 2 padding bytes and
        // must treat their absence as truncation.
        var data = Serialize([Entry(0x0000), Entry(0x0014, 0x10)]);
        Assert.Equal(46, data.Length);

        Assert.Throws<RvzFormatException>(() => ExceptionListParser.Parse(data, 1, alignTo4: true));
        // Without alignment the same bytes are perfectly valid.
        var (lists, _) = ExceptionListParser.Parse(data, 1, alignTo4: false);
        Assert.Equal(2, lists[0].Length);
    }

    [Fact]
    public void MultipleLists_AreParsedSequentially()
    {
        var lists = new[]
        {
            [Entry(0x0000)],
            new[] { Entry(0x0500, 0x20), Entry(0x0600, 0x30) },
            Array.Empty<HashExceptionEntry>()
        };
        var data = Serialize(lists);

        var (parsed, bytesUsed) = ExceptionListParser.Parse(data, 3, alignTo4: false);

        AssertListsEqual(lists, parsed);
        Assert.Equal(2 + 22 + 2 + 44 + 2, bytesUsed);
    }

    [Fact]
    public void LastListAligned_OnlyTheLastListIsPadded()
    {
        // alignTo4 pads only the final list; earlier lists keep their exact sizes.
        var lists = new[] { new[] { Entry(0x0000) }, new[] { Entry(0x0010, 0x40), Entry(0x0020, 0x50) } };
        var data = Serialize(lists).Concat(new byte[2]).ToArray(); // 70 bytes + 2 of padding
        Assert.Equal(72, data.Length);

        var (parsed, bytesUsed) = ExceptionListParser.Parse(data, 2, alignTo4: true);

        AssertListsEqual(lists, parsed);
        Assert.Equal(72, bytesUsed);
    }

    [Fact]
    public void Truncated_After_Count_Throws()
    {
        Assert.Throws<RvzFormatException>(() => ExceptionListParser.Parse("\0\0"u8, 2, alignTo4: false));
    }

    [Fact]
    public void Truncated_Count_Throws()
    {
        Assert.Throws<RvzFormatException>(() => ExceptionListParser.Parse([0x00], 1, alignTo4: false));
    }

    [Fact]
    public void Truncated_Entries_Throw()
    {
        // Declares 3 entries but carries only 2 (the tail of the buffer is short).
        var data = Serialize([Entry(0x0000), Entry(0x0014, 0x10)]);
        var full = new byte[2 + 3 * HashExceptionEntry.Size - 4]; // short by 4 bytes
        data.CopyTo(full, 0);
        full[0] = 0; // count is BE16 → 3
        full[1] = 3;

        Assert.Throws<RvzFormatException>(() => ExceptionListParser.Parse(full, 1, alignTo4: false));
    }

    [Fact]
    public void MaxSizeList_Parses()
    {
        var entries = new HashExceptionEntry[ExceptionListParser.MaxExceptionsPerList];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = Entry((ushort)i, (byte)(i % 251));
        }

        var data = Serialize(entries);
        var (lists, bytesUsed) = ExceptionListParser.Parse(data, 1, alignTo4: false);

        AssertEntriesEqual(entries, lists[0]);
        Assert.Equal(ExceptionListParser.MaxBytesPerList, bytesUsed);
    }
}