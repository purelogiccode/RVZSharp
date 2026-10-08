using RVZSharp.Blobs;
using RVZSharp.Files;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the FST parser (<see cref="DiscFileSystem"/>) and the decrypted partition view
/// (<see cref="PartitionReader"/>): GameCube discs and Wii partitions expose the same tree,
/// case-insensitive lookup and file bytes, mirroring Dolphin's FileSystemGCWii.
/// </summary>
public class DiscFileSystemTests
{
    /// <summary>Verifies that game cube parses tree and reads files.</summary>
    [Fact]
    public void GameCube_ParsesTreeAndReadsFiles()
    {
        var iso = BuildGcIso();
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        using var fs = DiscFileSystem.Open(blob);

        Assert.True(fs.Root.IsDirectory);
        Assert.True(fs.Root.IsRoot);
        Assert.Single(fs.Root.Children);
        Assert.Equal("files/", fs.Root.Children[0].Path);
        Assert.Equal(2, fs.Root.Children[0].Children.Count);

        var hello = fs.Find("files/hello.txt");
        Assert.NotNull(hello);
        Assert.False(hello.IsDirectory);
        Assert.Equal("files/hello.txt", hello.Path);
        Assert.Equal(HelloText.Length, hello.Size);

        using var contents = new MemoryStream();
        Assert.Equal(HelloText.Length, fs.CopyFileTo(hello, contents));
        Assert.Equal(HelloText, contents.ToArray());
    }

    /// <summary>Verifies that game cube find is case insensitive and accepts leading slash.</summary>
    [Fact]
    public void GameCube_FindIsCaseInsensitiveAndAcceptsLeadingSlash()
    {
        var iso = BuildGcIso();
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        using var fs = DiscFileSystem.Open(blob);

        Assert.Same(fs.Find("FILES/HELLO.TXT"), fs.Find("/files/hello.txt"));
        Assert.Same(fs.Root, fs.Find("/"));
        Assert.Null(fs.Find("files/missing.bin"));
        Assert.Null(fs.Find("missing/hello.txt"));
    }

    /// <summary>Verifies that Wii partition parses tree and reads files.</summary>
    [Fact]
    public void Wii_Partition_ParsesTreeAndReadsFiles()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        const int sectorCount = 130;
        var data = TestWiiIsoBuilder.RandomData(sectorCount, 7);

        // The decrypted boot sector carries the Wii magic and the FST fields.
        WriteBe32(data, 0x18, WiiVolume.WII_MAGIC);
        var fst = BuildWiiFst();
        const int fstOffset = 0x3000;
        const int helloOffset = 0x4000;
        const int dataOffset = 0x4200;
        WriteBe32(data, 0x424, fstOffset >> 2);
        WriteBe32(data, 0x428, (uint)(fst.Length >> 2));
        fst.CopyTo(data, fstOffset);
        HelloText.CopyTo(data, helloOffset);
        DataBytes.CopyTo(data, dataOffset);

        var iso = TestWiiIsoBuilder.Build(key, sectorCount, data);
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var partitions = WiiVolume.GetPartitions(blob);
        Assert.Single(partitions);

        // The FST fields live in the decrypted boot sector, not in the raw partition header.
        Assert.Equal((ulong)fstOffset, WiiVolume.GetFstOffset(blob, partitions[0]));
        Assert.Equal((ulong)fst.Length, WiiVolume.GetFstSize(blob, partitions[0]));

        using var fs = DiscFileSystem.Open(blob, partitions[0]);
        var hello = fs.Find("files/hello.txt");
        var bytes = fs.Find("files/data.bin");
        Assert.NotNull(hello);
        Assert.NotNull(bytes);

        using var helloContents = new MemoryStream();
        fs.CopyFileTo(hello, helloContents);
        Assert.Equal(HelloText, helloContents.ToArray());

        using var dataContents = new MemoryStream();
        fs.CopyFileTo(bytes, dataContents);
        Assert.Equal(DataBytes, dataContents.ToArray());
    }

    /// <summary>Verifies that partition reader decrypts boot sector.</summary>
    [Fact]
    public void PartitionReader_DecryptsBootSector()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        const int sectorCount = 70;
        var data = TestWiiIsoBuilder.RandomData(sectorCount, 7);
        var iso = TestWiiIsoBuilder.Build(key, sectorCount, data);

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var partition = WiiVolume.GetPartitions(blob)[0];
        using var view = new PartitionReader(blob, partition);

        Assert.Equal(sectorCount * 0x8000, view.Length);

        var firstSector = new byte[0x8000];
        Assert.Equal(firstSector.Length, view.ReadAt(0, firstSector));
        Assert.Equal(data.AsSpan(0, 0x7C00).ToArray(), firstSector.AsSpan(0, 0x7C00).ToArray());
    }

    /// <summary>Verifies that disc without file system throws.</summary>
    [Fact]
    public void DiscWithoutFileSystem_Throws()
    {
        var iso = new byte[0x420000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
    }

    private static readonly byte[] HelloText = "hello from the disc!\n"u8.ToArray();
    private static readonly byte[] DataBytes = [0x00, 0x01, 0x02, 0x03, 0xFE, 0xFF, 0x7F, 0x80];

    /// <summary>
    /// A GameCube ISO with one directory and two files. The FST lives at 0x3000, the file
    /// data at absolute disc offsets (GameCube entry offsets are not shifted).
    /// </summary>
    private static byte[] BuildGcIso()
    {
        const int fstOffset = 0x3000;
        const int helloOffset = 0x4000;
        const int dataOffset = 0x4200;

        var iso = new byte[0x200000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2; // GameCube disc magic
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        var fst = BuildFst(offsetShift: 0, helloOffset, dataOffset);
        WriteBe32(iso, 0x424, fstOffset >> 2);
        WriteBe32(iso, 0x428, (uint)(fst.Length >> 2));
        fst.CopyTo(iso, fstOffset);
        HelloText.CopyTo(iso, helloOffset);
        DataBytes.CopyTo(iso, dataOffset);
        return iso;
    }

    /// <summary>A Wii FST with the same tree; entry offsets are shifted by 2.</summary>
    private static byte[] BuildWiiFst()
    {
        return BuildFst(offsetShift: 2, helloOffset: 0x4000, dataOffset: 0x4200);
    }

    /// <summary>
    /// Builds an FST: root (0), directory "files" (1), "hello.txt" (2), "data.bin" (3);
    /// the string table follows the four entries.
    /// </summary>
    private static byte[] BuildFst(int offsetShift, int helloOffset, int dataOffset)
    {
        const int totalEntries = 4;
        var names = "\0files\0hello.txt\0data.bin\0"u8.ToArray();
        // The FST size is stored shifted by 2, so it must be 4-byte aligned.
        var size = (totalEntries * 12 + names.Length + 3) / 4 * 4;
        var fst = new byte[size];
        var nameTable = totalEntries * 12;
        names.CopyTo(fst, nameTable);

        // Root: directory, name offset 0, parent 0, subtree end = 4.
        WriteBe32(fst, 0, 0x01000000);
        WriteBe32(fst, 4, 0);
        WriteBe32(fst, 8, totalEntries);

        // Entry 1: directory "files", parent 0, subtree end = 4.
        WriteBe32(fst, 12, 0x01000000u | 1u);
        WriteBe32(fst, 16, 0);
        WriteBe32(fst, 20, totalEntries);

        // Entry 2: file "hello.txt" at helloOffset.
        WriteBe32(fst, 24, 7);
        WriteBe32(fst, 28, (uint)(helloOffset >> offsetShift));
        WriteBe32(fst, 32, (uint)HelloText.Length);

        // Entry 3: file "data.bin" at dataOffset.
        WriteBe32(fst, 36, 17);
        WriteBe32(fst, 40, (uint)(dataOffset >> offsetShift));
        WriteBe32(fst, 44, (uint)DataBytes.Length);

        return fst;
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
