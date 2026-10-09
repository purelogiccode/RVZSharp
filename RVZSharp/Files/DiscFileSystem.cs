using System.Buffers.Binary;
using System.Text;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Files;

/// <summary>
/// Parses a GameCube or Wii partition file system table (FST, Dolphin: FileSystemGCWii) and
/// exposes the file tree plus file data reads over the decoded disc bytes. Wii partitions are
/// read through a decrypted <see cref="PartitionReader"/> view, so file offsets match Dolphin's
/// partition-relative semantics.
/// </summary>
public sealed class DiscFileSystem : IDisposable
{
    private const int FstEntrySize = 12; // three 32-bit integers per entry
    private const int MaxFstSize = 128 * 1024 * 1024; // Dolphin: ARBITRARY_FILE_SYSTEM_SIZE_LIMIT

    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();

    private readonly IBlobReader _reader;
    private readonly bool _ownsReader;

    private DiscFileSystem(IBlobReader reader, bool ownsReader, DiscFileInfo root,
        ulong fstOffset, ulong fstSize)
    {
        _reader = reader;
        _ownsReader = ownsReader;
        Root = root;
        FstOffset = fstOffset;
        FstSize = fstSize;
    }

    /// <summary>The root directory of the file system.</summary>
    public DiscFileInfo Root { get; }

    /// <summary>The FST offset in the reader's view (shifted value from the boot header).</summary>
    public ulong FstOffset { get; }

    /// <summary>The FST size in bytes.</summary>
    public ulong FstSize { get; }

    /// <summary>
    /// Opens the file system of a GameCube disc (or any disc whose partition data is stored
    /// at offset 0, i.e. not a Wii partition).
    /// </summary>
    /// <param name="disc">The decoded disc image.</param>
    /// <returns>The parsed file system.</returns>
    /// <exception cref="RvzFormatException">The disc has no valid GameCube/Wii file system.</exception>
    public static DiscFileSystem Open(IBlobReader disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        return Parse(disc, ownsReader: false);
    }

    /// <summary>Opens the file system of a Wii partition through a decrypted view.</summary>
    /// <param name="disc">The decoded disc image.</param>
    /// <param name="partition">The partition to open (from <see cref="WiiVolume.GetPartitions"/>).</param>
    /// <returns>The parsed file system; dispose it to release the decryption context.</returns>
    /// <exception cref="RvzFormatException">The partition has no valid file system.</exception>
    public static DiscFileSystem Open(IBlobReader disc, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disc);
        var view = new PartitionReader(disc, partition);
        try
        {
            return Parse(view, ownsReader: true);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds a file or directory by path (Dolphin: FindFileInfo). The path uses '/'
    /// separators, may start with '/', and is matched case-insensitively.
    /// </summary>
    /// <param name="path">The path to look up (empty or "/" returns the root).</param>
    /// <returns>The matching entry, or null when it does not exist.</returns>
    public DiscFileInfo? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            DiscFileInfo? next = null;
            foreach (var child in current.Children)
            {
                if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Copies the contents of a file into <paramref name="destination"/>.</summary>
    /// <param name="file">The file to read (from <see cref="Find"/> or the tree).</param>
    /// <param name="destination">The stream that receives the file bytes.</param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The number of bytes copied (the file size).</returns>
    /// <exception cref="ArgumentException"><paramref name="file"/> is a directory.</exception>
    /// <exception cref="RvzFormatException">The file data ended early (corrupt image).</exception>
    public long CopyFileTo(DiscFileInfo file, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (file.IsDirectory)
        {
            throw new ArgumentException("Directories have no file data.", nameof(file));
        }

        var remaining = file.Size;
        var position = file.Offset;
        var buffer = new byte[(int)Math.Min(1 << 20, Math.Max(1, remaining))];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = _reader.ReadAt(position, buffer.AsSpan(0, take));
            if (read <= 0)
            {
                throw new RvzFormatException(
                    $"File data ended at 0x{position:X} ({remaining} bytes missing).");
            }

            destination.Write(buffer, 0, read);
            position += read;
            remaining -= read;
        }

        return file.Size;
    }

    private static DiscFileSystem Parse(IBlobReader view, bool ownsReader)
    {
        var offsetShift = WiiVolume.TryGetOffsetShift(view)
                          ?? throw new RvzFormatException(
                              "The image has no GameCube or Wii disc magic; it has no file system.");
        var fstOffset = ReadSwappedAndShifted(view, 0x424, offsetShift)
                        ?? throw new RvzFormatException("The boot header has no FST offset.");
        var fstSize = ReadSwappedAndShifted(view, 0x428, offsetShift)
                      ?? throw new RvzFormatException("The boot header has no FST size.");
        if (fstSize < FstEntrySize)
        {
            throw new RvzFormatException($"The file system table is too small ({fstSize} bytes).");
        }

        if (fstSize > MaxFstSize)
        {
            throw new RvzFormatException($"The file system table is abnormally large ({fstSize} bytes).");
        }

        var fst = new byte[fstSize];
        if (view.ReadAt((long)fstOffset, fst) != fst.Length)
        {
            throw new RvzFormatException("The file system table is truncated.");
        }

        // Dolphin: if the FST's final byte isn't 0, name reads can run past the end.
        if (fst[^1] != 0)
        {
            throw new RvzFormatException("The file system table does not end with a null byte.");
        }

        var totalEntries = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        if (totalEntries == 0 || (ulong)totalEntries * FstEntrySize > fstSize)
        {
            throw new RvzFormatException(
                $"The file system table declares {totalEntries} entries, which do not fit its size.");
        }

        var nameTableOffset = (int)totalEntries * FstEntrySize;
        var nodes = new DiscFileInfo[totalEntries];
        var rawOffsets = new uint[totalEntries];
        var ends = new uint[totalEntries];
        for (uint i = 0; i < totalEntries; i++)
        {
            var entry = fst.AsSpan((int)i * FstEntrySize, FstEntrySize);
            var nameField = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var isDirectory = (nameField & 0xFF000000) != 0;
            var nameOffset = (int)(nameField & 0xFFFFFF);
            if (nameTableOffset + nameOffset >= fst.Length)
            {
                throw new RvzFormatException(
                    $"File system entry {i} has an impossibly large name offset.");
            }

            var name = ReadName(fst, nameTableOffset + nameOffset);
            rawOffsets[i] = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            ends[i] = isDirectory ? size : i + 1;

            var offset = (long)((ulong)rawOffsets[i] << offsetShift);
            nodes[i] = new DiscFileInfo(name, isDirectory, isDirectory ? 0 : offset,
                size, parent: null);
        }

        if (!nodes[0].IsDirectory)
        {
            throw new RvzFormatException("The file system root is not a directory.");
        }

        // Link the tree: entries are in DFS order, so a directory's subtree is the contiguous
        // range [index + 1, dirEnd). The stack holds the open directories.
        var stack = new Stack<uint>();
        stack.Push(0);
        for (uint i = 1; i < totalEntries; i++)
        {
            while (i >= ends[stack.Peek()])
            {
                stack.Pop();
            }

            var parentIndex = stack.Peek();
            var node = nodes[i];
            var parent = nodes[parentIndex];

            if (node.IsDirectory)
            {
                if (rawOffsets[i] != parentIndex)
                {
                    throw new RvzFormatException(
                        $"Directory entry {i} names {rawOffsets[i]} as its parent, not {parentIndex}.");
                }

                if (ends[i] <= i || ends[i] > ends[parentIndex])
                {
                    throw new RvzFormatException(
                        $"Directory entry {i} has an impossible subtree range.");
                }
            }

            // The Path property needs the parent, so rebuild the node with it.
            var linked = new DiscFileInfo(node.Name, node.IsDirectory, node.Offset, node.Size, parent);
            nodes[i] = linked;
            parent.AddChild(linked);
            if (linked.IsDirectory)
            {
                stack.Push(i);
            }
        }

        return new DiscFileSystem(view, ownsReader, nodes[0], fstOffset, fstSize);
    }

    private static ulong? ReadSwappedAndShifted(IBlobReader view, ulong offset, int shift)
    {
        Span<byte> bytes = stackalloc byte[4];
        if ((ulong)view.Length < offset + 4 || view.ReadAt((long)offset, bytes) != 4)
        {
            return null;
        }

        return (ulong)BinaryPrimitives.ReadUInt32BigEndian(bytes) << shift;
    }

    private static string ReadName(byte[] fst, int offset)
    {
        var end = offset;
        while (end < fst.Length && fst[end] != 0)
        {
            end++;
        }

        return ShiftJis.GetString(fst, offset, end - offset);
    }

    private static Encoding CreateShiftJisEncoding()
    {
        // FST names are Shift-JIS (Dolphin: SHIFTJISToUTF8); the code pages provider is part
        // of the .NET runtime, and the fallback keeps malformed names readable.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Disposes the decrypted partition view created by <see cref="Open(IBlobReader, Partition)"/>.</summary>
    public void Dispose()
    {
        if (_ownsReader)
        {
            _reader.Dispose();
        }
    }
}
