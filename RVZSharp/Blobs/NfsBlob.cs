using System.Security.Cryptography;
using RVZSharp.IO;
using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>
/// The NFS format used by Wii U eShop downloads (Dolphin: NFSFileReader, magic "EGGS"):
/// a 0x200-byte header with big-endian LBA ranges, followed by 0x8000-byte blocks that are
/// AES-128-CBC encrypted with a key from <c>code/htk.bin</c> (sibling of the <c>content</c>
/// directory) and an IV of 8 zero bytes plus the big-endian block index. Blocks outside the
/// ranges decode to zeroes; the decoded size is a lower bound of the disc size.
/// </summary>
public sealed class NfsBlob : IBlobReader
{
    private const int HeaderSize = 0x200;
    private const long BlockSizeValue = 0x8000;
    private const long MaxFileSize = 0xFA00000;
    private const int BlocksPerFile = (int)(MaxFileSize / BlockSizeValue); // 0x1F40
    private const int MaxLbaRanges = 61;
    private const int KeySize = 16;

    private readonly Stream[] _files;
    private readonly bool _leaveOpen;
    private readonly byte[] _key;
    private readonly (uint Start, uint Num)[] _ranges;
    private readonly Aes _aes;

    private NfsBlob(Stream[] files, bool leaveOpen, byte[] key, (uint Start, uint Num)[] ranges,
        long length)
    {
        _files = files;
        _leaveOpen = leaveOpen;
        _key = key;
        _ranges = ranges;
        Length = length;
        _aes = Aes.Create();
        _aes.Key = key;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
    }

    public BlobType Type => BlobType.Nfs;
    public long Length { get; }
    public int BlockSize => (int)BlockSizeValue;

    /// <summary>
    /// Opens an NFS file. When <paramref name="filePath"/> points at <c>hif_000000.nfs</c>,
    /// the AES key is loaded from the sibling <c>code/htk.bin</c> and continuation files
    /// (<c>hif_000001.nfs</c>, …) are opened automatically; otherwise the key is required via
    /// <see cref="Open(Stream, ReadOnlySpan{byte}, bool)"/> and only the given stream is used.
    /// </summary>
    public static NfsBlob Open(Stream stream, string? filePath = null, bool leaveOpen = false)
    {
        if (filePath != null)
        {
            var key = ReadKeyFromDisk(filePath);
            return Open(stream, key, leaveOpen, filePath);
        }

        throw new RvzUnsupportedException(
            "The NFS format needs the 16-byte AES key from code/htk.bin; pass the file path of " +
            "hif_000000.nfs (or use Open(stream, key)).");
    }

    /// <summary>Opens an NFS file with an explicit 16-byte AES key (single-file mode).</summary>
    public static NfsBlob Open(Stream stream, ReadOnlySpan<byte> key, bool leaveOpen = false) =>
        Open(stream, key, leaveOpen, filePath: null);

    private static NfsBlob Open(Stream stream, ReadOnlySpan<byte> key, bool leaveOpen, string? filePath)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The NFS stream must be seekable.", nameof(stream));
        }

        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The NFS key must be {KeySize} bytes.", nameof(key));
        }

        var header = new byte[HeaderSize];
        if (!ReadExactlyAt(stream, 0, header))
        {
            throw new RvzFormatException("The file is too short to contain an NFS header.");
        }

        if (!header.AsSpan(0, 4).SequenceEqual("EGGS"u8))
        {
            throw new RvzFormatException(
                $"Bad NFS magic: expected \"EGGS\", got {System.Text.Encoding.ASCII.GetString(header, 0, 4)}.");
        }

        var rangeCount = Math.Min(ReadBe32(header, 0x10), (uint)MaxLbaRanges);
        var ranges = new (uint Start, uint Num)[rangeCount];
        ulong totalBlocks = 0;
        uint greatestBlockIndex = 0;
        for (var i = 0; i < rangeCount; i++)
        {
            var start = ReadBe32(header, 0x14 + i * 8);
            var num = ReadBe32(header, 0x18 + i * 8);
            ranges[i] = (start, num);
            totalBlocks += num;
            // Guard against u32 wraparound in start + num (hostile range tables).
            if (num > 0 && start > uint.MaxValue - num)
            {
                throw new RvzFormatException(
                    $"NFS LBA range {i} wraps past the 32-bit address space.");
            }

            greatestBlockIndex = Math.Max(greatestBlockIndex, start + num);
        }

        var expectedRawSize = (ulong)HeaderSize + totalBlocks * (ulong)BlockSizeValue;

        // Open continuation files (Dolphin: OpenFiles). The data stream is the concatenation
        // of the files, each 0xFA00000 bytes, with the last 0x200 bytes of every full file
        // belonging to the next file (its header region).
        var files = new List<Stream> { stream };
        if (filePath != null)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (directory == null)
            {
                throw new RvzFormatException("Cannot resolve the NFS file's directory.");
            }

            var fileCount = (int)((expectedRawSize + (ulong)MaxFileSize - 1) / (ulong)MaxFileSize);
            var rawSize = stream.Length;
            for (var i = 1; i < fileCount; i++)
            {
                var childPath = Path.Combine(directory, $"hif_{i:D6}.nfs");
                if (!File.Exists(childPath))
                {
                    throw new RvzFormatException($"Failed to open the NFS continuation file {childPath}.");
                }

                var child = File.OpenRead(childPath);
                files.Add(child);
                rawSize += child.Length;
            }

            if (rawSize < (long)expectedRawSize)
            {
                foreach (var file in files.Skip(1))
                {
                    file.Dispose();
                }

                throw new RvzFormatException(
                    $"Expected the NFS files to sum to at least {expectedRawSize} bytes, got {rawSize}.");
            }
        }
        else
        {
            // Single-file mode: the stream itself must cover the expected raw size — Dolphin
            // validates the sum in every mode (NFSBlob.cpp:96-103).
            if (stream.Length < (long)expectedRawSize)
            {
                throw new RvzFormatException(
                    $"Expected the NFS data to be at least {expectedRawSize} bytes, got {stream.Length}.");
            }
        }

        return new NfsBlob(files.ToArray(), leaveOpen, key.ToArray(), ranges,
            (long)greatestBlockIndex * BlockSizeValue);
    }

    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var block = (int)(position / BlockSizeValue);
            var offsetInBlock = (int)(position % BlockSizeValue);
            var take = (int)Math.Min(Math.Min(buffer.Length, BlockSizeValue - offsetInBlock), Length - position);

            var decrypted = DecryptBlock(block);
            decrypted.AsSpan(offsetInBlock, take).CopyTo(buffer);

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    private byte[] DecryptBlock(long logicalBlockIndex)
    {
        var output = new byte[BlockSizeValue];

        var physical = ToPhysicalBlockIndex(logicalBlockIndex);
        if (physical < 0)
        {
            // The block isn't physically present: all zeroes.
        }
        else
        {
            var fileIndex = physical / BlocksPerFile;
            var blockInFile = (int)(physical % BlocksPerFile);
            var offsetInFile = HeaderSize + blockInFile * BlockSizeValue;

            if (blockInFile == BlocksPerFile - 1)
            {
                // The last block of a full file: its final 0x200 bytes live at the start of
                // the next file (that file's header region). Without a continuation file the
                // block is unreadable — fail instead of indexing past the file list
                // (Dolphin: NFSBlob.cpp:214-215 always has the full file set).
                if (fileIndex + 1 >= _files.Length)
                {
                    throw new RvzFormatException(
                        $"NFS block {logicalBlockIndex} needs a continuation file that is not available.");
                }

                if (!ReadExactlyAt(_files[fileIndex], offsetInFile,
                        output.AsSpan(0, (int)(BlockSizeValue - HeaderSize))) ||
                    !ReadExactlyAt(_files[fileIndex + 1], 0,
                        output.AsSpan((int)(BlockSizeValue - HeaderSize), HeaderSize)))
                {
                    throw new RvzFormatException(
                        $"NFS block {logicalBlockIndex} is truncated across its file boundary.");
                }
            }
            else
            {
                if (!ReadExactlyAt(_files[fileIndex], offsetInFile, output))
                {
                    throw new RvzFormatException(
                        $"NFS block {logicalBlockIndex} is truncated.");
                }
            }

            var iv = new byte[16];
            WriteBe64(iv, 8, (ulong)logicalBlockIndex);
            using var decryptor = _aes.CreateDecryptor(_aes.Key, iv);
            decryptor.TransformBlock(output, 0, output.Length, output, 0);
        }

        // Mark the disc as unencrypted so that the volume is treated as a plain disc
        // (Dolphin: sets byte 0x61 of the first block's header to 1).
        if (logicalBlockIndex == 0)
        {
            output[0x61] = 1;
        }

        return output;
    }

    private long ToPhysicalBlockIndex(long logicalBlockIndex)
    {
        long physicalBlocksSoFar = 0;
        foreach (var (start, num) in _ranges)
        {
            if (logicalBlockIndex >= start && logicalBlockIndex < start + num)
            {
                return physicalBlocksSoFar + (logicalBlockIndex - start);
            }

            physicalBlocksSoFar += num;
        }

        return -1;
    }

    private static byte[] ReadKeyFromDisk(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory == null)
        {
            throw new RvzFormatException("Cannot resolve the NFS file's directory.");
        }

        // The NFS file must be named hif_000000.nfs and live in a directory named "content"
        // (Dolphin: NFSBlob.cpp:129-132 + ReadKey); the key is <parent>/code/htk.bin.
        if (!string.Equals(Path.GetFileName(filePath), "hif_000000.nfs", StringComparison.Ordinal))
        {
            throw new RvzFormatException(
                $"The NFS file must be named hif_000000.nfs: {filePath}");
        }

        var contentDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(contentDir), "content", StringComparison.Ordinal))
        {
            throw new RvzFormatException(
                $"The NFS file is not inside a directory named 'content': {filePath}");
        }

        var keyPath = Path.Combine(Path.GetDirectoryName(contentDir) ?? "", "code", "htk.bin");
        if (!File.Exists(keyPath))
        {
            throw new RvzFormatException($"Failed to read the NFS key from {keyPath}.");
        }

        var key = File.ReadAllBytes(keyPath);
        if (key.Length < KeySize)
        {
            throw new RvzFormatException($"The NFS key file {keyPath} is shorter than {KeySize} bytes.");
        }

        return key[..KeySize];
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteBe64(byte[] data, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            data[offset + i] = (byte)(value >> (56 - 8 * i));
        }
    }

    private static bool ReadExactlyAt(Stream stream, long position, Span<byte> buffer)
    {
        if (stream.Position != position)
        {
            stream.Position = position;
        }

        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
            {
                throw new RvzFormatException("The NFS data is truncated.");
            }

            total += read;
        }

        return true;
    }

    public void Dispose()
    {
        _aes.Dispose();
        // Continuation files were opened internally and must always be disposed; only the
        // caller's stream (files[0]) is exempt when leaveOpen is set.
        for (var i = 1; i < _files.Length; i++)
        {
            _files[i].Dispose();
        }

        if (!_leaveOpen)
        {
            _files[0].Dispose();
        }
    }
}
