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

    /// <summary>
    /// Verifies that boot-header offsets are shifted per disc type: GameCube offsets are byte
    /// offsets, while Wii partition offsets are stored in 4-byte units
    /// (Dolphin: Volume::GetOffsetShift).
    /// </summary>
    [Fact]
    public void BootDolOffset_UsesDiscTypeShift()
    {
        var gc = BuildGcIso();
        WriteBe32(gc, 0x420, 0x800);
        using (var gcBlob = PlainBlob.Open(new MemoryStream(gc)))
        {
            Assert.Equal(0x800UL, WiiVolume.GetBootDolOffset(gcBlob));
        }

        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        const int sectorCount = 130;
        var data = TestWiiIsoBuilder.RandomData(sectorCount, 7);
        WriteBe32(data, 0x18, WiiVolume.WII_MAGIC);
        WriteBe32(data, 0x420, 0x800 >> 2);
        var iso = TestWiiIsoBuilder.Build(key, sectorCount, data);
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var partition = WiiVolume.GetPartitions(blob)[0];
        using var view = new PartitionReader(blob, partition);
        Assert.Equal(0x800UL, WiiVolume.GetBootDolOffset(view));
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

        Assert.Equal(sectorCount * 0x7C00, view.Length);

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

    /// <summary>Verifies that a boot header without FST fields is rejected.</summary>
    [Fact]
    public void GameCube_MissingFstFields_Throws()
    {
        var iso = new byte[0x100];
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("FST offset", exception.Message);
    }

    /// <summary>Verifies that an FST smaller than one entry is rejected.</summary>
    [Fact]
    public void GameCube_FstSizeTooSmall_Throws()
    {
        using var blob = OpenGcWithFst(BuildSingleFileFst(0x4000, 8), fstSize: 8);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("too small", exception.Message);
    }

    /// <summary>Verifies that an abnormally large FST size is rejected before reading.</summary>
    [Fact]
    public void GameCube_FstSizeAbnormallyLarge_Throws()
    {
        using var blob = OpenGcWithFst(BuildSingleFileFst(0x4000, 8), fstSize: 128 * 1024 * 1024 + 1);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("abnormally large", exception.Message);
    }

    /// <summary>Verifies that an FST extending past the image is rejected.</summary>
    [Fact]
    public void GameCube_TruncatedFst_Throws()
    {
        var fst = BuildSingleFileFst(0x4000, 8);
        using var blob = OpenGcWithFst(fst, fstOffset: 0x1FFFF0, fstSize: (uint)fst.Length);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("truncated", exception.Message);
    }

    /// <summary>Verifies that an FST whose last byte is not NUL is rejected.</summary>
    [Fact]
    public void GameCube_FstWithoutTrailingNull_Throws()
    {
        var fst = BuildSingleFileFst(0x4000, 8);
        fst[^1] = 1;
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("null byte", exception.Message);
    }

    /// <summary>Verifies that an FST declaring zero entries is rejected.</summary>
    [Fact]
    public void GameCube_ZeroEntryFst_Throws()
    {
        var fst = new byte[16];
        WriteBe32(fst, 0, 0x01000000);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("0 entries", exception.Message);
    }

    /// <summary>Verifies that an entry count that does not fit the FST size is rejected.</summary>
    [Fact]
    public void GameCube_EntryCountTooLarge_Throws()
    {
        var fst = new byte[16];
        WriteBe32(fst, 0, 0x01000000);
        WriteBe32(fst, 8, 1000);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("do not fit", exception.Message);
    }

    /// <summary>Verifies that an impossible name offset is rejected.</summary>
    [Fact]
    public void GameCube_ImpossibleNameOffset_Throws()
    {
        var fst = new byte[16];
        WriteBe32(fst, 0, 0x01FFFFFF); // directory with a huge name offset
        WriteBe32(fst, 8, 1);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("name offset", exception.Message);
    }

    /// <summary>Verifies that an FST whose root entry is a file is rejected.</summary>
    [Fact]
    public void GameCube_RootNotDirectory_Throws()
    {
        var fst = new byte[16];
        WriteBe32(fst, 0, 0); // file, not directory
        WriteBe32(fst, 8, 1);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("root is not a directory", exception.Message);
    }

    /// <summary>Verifies that a directory naming the wrong parent is rejected.</summary>
    [Fact]
    public void GameCube_DirectoryParentMismatch_Throws()
    {
        var fst = BuildThreeEntryFst(directoryParent: 5, directoryEnd: 3);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("as its parent", exception.Message);
    }

    /// <summary>Verifies that an impossible directory subtree range is rejected.</summary>
    [Fact]
    public void GameCube_ImpossibleSubtreeRange_Throws()
    {
        var fst = BuildThreeEntryFst(directoryParent: 0, directoryEnd: 1);
        using var blob = OpenGcWithFst(fst);
        var exception = Assert.Throws<RvzFormatException>(() => DiscFileSystem.Open(blob));
        Assert.Contains("impossible subtree", exception.Message);
    }

    /// <summary>Verifies that copying a directory throws.</summary>
    [Fact]
    public void CopyFileTo_Directory_Throws()
    {
        var iso = BuildGcIso();
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        using var fs = DiscFileSystem.Open(blob);

        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => fs.CopyFileTo(fs.Root, output));
    }

    /// <summary>Verifies that a file whose data ends early is rejected.</summary>
    [Fact]
    public void CopyFileTo_TruncatedData_Throws()
    {
        // The file claims 0x1000 bytes at 0x1FFFF0, but the image ends at 0x200000.
        var fst = BuildSingleFileFst(fileOffset: 0x1FFFF0, fileSize: 0x1000);
        using var blob = OpenGcWithFst(fst);
        using var fs = DiscFileSystem.Open(blob);
        var file = fs.Find("file.bin");
        Assert.NotNull(file);

        using var output = new MemoryStream();
        var exception = Assert.Throws<RvzFormatException>(() => fs.CopyFileTo(file, output));
        Assert.Contains("ended at", exception.Message);
    }

    /// <summary>Verifies that Find rejects a null path.</summary>
    [Fact]
    public void Find_Null_Throws()
    {
        var iso = BuildGcIso();
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        using var fs = DiscFileSystem.Open(blob);
        Assert.Throws<ArgumentNullException>(() => fs.Find(null!));
    }

    /// <summary>Opens a synthetic GameCube image carrying the given FST bytes.</summary>
    private static PlainBlob OpenGcWithFst(byte[] fst, uint fstOffset = 0x3000, uint? fstSize = null)
    {
        var iso = new byte[0x200000];
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
        WriteBe32(iso, 0x424, fstOffset);
        WriteBe32(iso, 0x428, fstSize ?? (uint)fst.Length);
        if (fstOffset + (ulong)fst.Length <= (ulong)iso.Length)
        {
            fst.CopyTo(iso, (int)fstOffset);
        }

        return PlainBlob.Open(new MemoryStream(iso));
    }

    /// <summary>Builds a two-entry FST: root directory + one file.</summary>
    private static byte[] BuildSingleFileFst(uint fileOffset, uint fileSize)
    {
        var names = "\0file.bin\0"u8.ToArray();
        var fst = new byte[24 + names.Length + 2];
        WriteBe32(fst, 0, 0x01000000);
        WriteBe32(fst, 8, 2);
        WriteBe32(fst, 12, 1); // file name offset
        WriteBe32(fst, 16, fileOffset);
        WriteBe32(fst, 20, fileSize);
        names.CopyTo(fst, 24);
        return fst;
    }

    /// <summary>Builds a three-entry FST: root, one directory and one file (for validation cases).</summary>
    private static byte[] BuildThreeEntryFst(uint directoryParent, uint directoryEnd)
    {
        var names = "\0dir\0file.bin\0"u8.ToArray();
        var fst = new byte[36 + names.Length + 2];
        WriteBe32(fst, 0, 0x01000000); // root, subtree end 3
        WriteBe32(fst, 8, 3);
        WriteBe32(fst, 12, 0x01000000u | 1u); // directory "dir"
        WriteBe32(fst, 16, directoryParent);
        WriteBe32(fst, 20, directoryEnd);
        WriteBe32(fst, 24, 5); // file "file.bin"
        WriteBe32(fst, 28, 0x4000);
        WriteBe32(fst, 32, 8);
        names.CopyTo(fst, 36);
        return fst;
    }

    private static readonly byte[] HelloText = "hello from the disc!\n"u8.ToArray();
    private static readonly byte[] DataBytes = [0x00, 0x01, 0x02, 0x03, 0xFE, 0xFF, 0x7F, 0x80];

    /// <summary>
    /// A GameCube ISO with one directory and two files. The FST lives at 0x3000 and the file
    /// data at absolute disc offsets: GameCube boot-header and entry offsets are not shifted
    /// (Dolphin: Volume::GetOffsetShift returns 0 for GameCube).
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
        WriteBe32(iso, 0x424, fstOffset);
        WriteBe32(iso, 0x428, (uint)fst.Length);
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
        // Wii stores the FST size shifted by 2, so it must be 4-byte aligned.
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
