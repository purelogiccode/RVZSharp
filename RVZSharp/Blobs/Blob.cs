using RVZSharp.Interfaces;
using RVZSharp.IO;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Blobs;

/// <summary>
/// Opens any supported disc image format by sniffing the first bytes (Dolphin:
/// CreateBlobReader). Every supported container starts with a 4-byte magic; a file that
/// starts with a recognized magic is always parsed as that container (a parse failure
/// throws an <see cref="RvzException"/>), and only files with no recognizable magic are
/// treated as a plain ISO — including split plain ISOs (<c>game.part0.iso</c> +
/// <c>game.part1.iso</c> + …, Dolphin: SplitPlainFileReader). Whether the bytes are a real
/// GameCube/Wii disc is answered by <see cref="RvzWriter.Write(IBlobReader, Stream, RvzWriteOptions, IProgress{double}, CancellationToken)"/>'s disc-header validation.
/// </summary>
public static class Blob
{
    private const string SplitPlainPart0Suffix = ".part0.iso";

    /// <summary>
    /// Detects and opens the container format of <paramref name="stream"/> and returns a reader
    /// that decodes the original disc image bytes.
    /// </summary>
    /// <param name="stream">Seekable stream of the disc image file.</param>
    /// <param name="filePath">
    /// Optional path of the file. Used to locate NFS <c>code/htk.bin</c> and
    /// <c>hif_00000X.nfs</c> files, split WBFS continuation parts, and split plain ISO parts
    /// (a path ending in <c>.part0.iso</c>).
    /// </param>
    /// <param name="leaveOpen">Whether disposing the reader leaves <paramref name="stream"/> open.</param>
    /// <exception cref="RvzFormatException">
    /// The file is too short to contain a magic number, or it starts with a recognized
    /// container magic whose header is corrupt (a corrupt container never falls back to
    /// being treated as a plain ISO).
    /// </exception>
    /// <exception cref="RvzUnsupportedException">
    /// The file is an RVZ/WIA container with a version newer than this library supports.
    /// </exception>
    public static IBlobReader Open(Stream stream, string? filePath = null, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        Span<byte> magic = stackalloc byte[4];
        if (!ReadExactlyAt(stream, 0, magic))
        {
            throw new RvzFormatException("The file is too short to contain a magic number.");
        }

        if (magic.SequenceEqual("RVZ\x01"u8))
        {
            return RvzReader.Open(stream, leaveOpen);
        }

        if (magic.SequenceEqual("WIA\x01"u8))
        {
            return RvzReader.OpenWia(stream, leaveOpen);
        }

        if (magic.SequenceEqual("CISO"u8))
        {
            return CisoBlob.Open(stream, leaveOpen);
        }

        if (magic.SequenceEqual(GczBlob.Magic))
        {
            return GczBlob.Open(stream, leaveOpen);
        }

        if (magic.SequenceEqual("WBFS"u8))
        {
            return WbfsBlob.Open(stream, filePath, leaveOpen);
        }

        if (magic.SequenceEqual("EGGS"u8))
        {
            return NfsBlob.Open(stream, filePath, leaveOpen);
        }

        // TGC's magic (0xA2380FAE) is the one little-endian field in the otherwise
        // big-endian header, so the on-disk bytes are AE 0F 38 A2 (Dolphin compares a native
        // u32 read against TGC_MAGIC: Blob.cpp:234, TGCBlob.cpp:50).
        if (magic.SequenceEqual(new byte[] { 0xAE, 0x0F, 0x38, 0xA2 }))
        {
            return TgcBlob.Open(stream, leaveOpen);
        }

        return OpenSplitPlain(stream, filePath, leaveOpen);
    }

    /// <summary>
    /// Opens a plain ISO, concatenating the continuation parts of a split image
    /// (<c>game.part0.iso</c> + <c>game.part1.iso</c> + …) when the path carries the split
    /// naming (Dolphin: SplitPlainFileReader). A zero-size part or an unreadable
    /// continuation makes the image fall back to the first part alone, like Dolphin.
    /// </summary>
    private static IBlobReader OpenSplitPlain(Stream firstPart, string? filePath, bool leaveOpen)
    {
        if (filePath is null || !filePath.EndsWith(SplitPlainPart0Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return PlainBlob.Open(firstPart, leaveOpen);
        }

        var basePath = filePath[..^SplitPlainPart0Suffix.Length];
        var parts = new List<Stream> { firstPart };
        try
        {
            for (var index = 1;; index++)
            {
                var partPath = $"{basePath}.part{index}.iso";
                if (!File.Exists(partPath))
                {
                    break;
                }

                var part = File.OpenRead(partPath);
                if (part.Length == 0)
                {
                    // Dolphin treats a zero-size part as "not a split file" and falls back
                    // to the first part alone (SplitFileBlob.cpp:40-41).
                    part.Dispose();
                    DisposeParts(parts, 1);
                    return PlainBlob.Open(firstPart, leaveOpen);
                }

                parts.Add(part);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DisposeParts(parts, 1);
            return PlainBlob.Open(firstPart, leaveOpen);
        }

        if (parts.Count == 1)
        {
            return PlainBlob.Open(firstPart, leaveOpen);
        }

        // The wrapper owns the continuation parts and follows leaveOpen for the first part;
        // PlainBlob then owns the wrapper.
        return PlainBlob.Open(new MultiPartStream(parts, leaveOpen), leaveOpen: false);
    }

    private static void DisposeParts(List<Stream> parts, int firstIndex)
    {
        for (var i = firstIndex; i < parts.Count; i++)
        {
            parts[i].Dispose();
        }
    }

    /// <summary>
    /// Opens a disc image file by path with automatic format detection. The returned reader
    /// owns the file stream; disposing the reader closes it.
    /// </summary>
    /// <param name="path">Path of the disc image file (RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS or a plain ISO).</param>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="RvzFormatException">
    /// The file is too short to contain a magic number, or it starts with a recognized
    /// container magic whose header is corrupt (a corrupt container never falls back to
    /// being treated as a plain ISO).
    /// </exception>
    /// <exception cref="RvzUnsupportedException">
    /// The file is an RVZ/WIA container with a version newer than this library supports.
    /// </exception>
    public static IBlobReader Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return Open(stream, path, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a disc image file by path with automatic format detection, supplying the NFS
    /// AES key explicitly (only used when the file is an NFS image; other formats ignore it).
    /// </summary>
    /// <param name="path">Path of the disc image file.</param>
    /// <param name="nfsKey">The 16-byte AES key used to decrypt NFS images.</param>
    public static IBlobReader Open(string path, ReadOnlySpan<byte> nfsKey)
    {
        return Open(File.OpenRead(path), nfsKey, leaveOpen: false);
    }

    /// <summary>
    /// Opens an NFS file (magic "EGGS") with an explicit 16-byte AES key; all other formats
    /// ignore the key. Use <see cref="Open(Stream, string?, bool)"/> to load the key from
    /// <c>code/htk.bin</c> instead.
    /// </summary>
    public static IBlobReader Open(Stream stream, ReadOnlySpan<byte> nfsKey, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        Span<byte> magic = stackalloc byte[4];
        if (!ReadExactlyAt(stream, 0, magic))
        {
            throw new RvzFormatException("The file is too short to contain a magic number.");
        }

        if (magic.SequenceEqual("EGGS"u8))
        {
            return NfsBlob.Open(stream, nfsKey, leaveOpen);
        }

        return Open(stream, filePath: null, leaveOpen);
    }

    /// <summary>
    /// Detects the disc type of an opened blob from the GameCube/Wii magic in the decoded
    /// disc header (Wii at 0x18, GameCube at 0x1C; Dolphin: TryCreateDisc). Unlike the
    /// container magic sniffing in <see cref="Open(Stream, string?, bool)"/>, this inspects
    /// the decoded disc bytes, so it answers "is this actually a GameCube/Wii disc?" for
    /// every input format. <see cref="RvzWriter.Write(IBlobReader, Stream, RvzWriteOptions, IProgress{double}, CancellationToken)"/> uses the same check before writing.
    /// </summary>
    /// <param name="reader">The blob to inspect.</param>
    /// <returns><see cref="DiscType.Wii"/>, <see cref="DiscType.GameCube"/>, or
    /// <see cref="DiscType.Unknown"/> when the image carries neither disc magic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static DiscType GetDiscType(IBlobReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return WiiVolume.GetDiscType(reader);
    }

    /// <summary>
    /// True when the decoded disc image carries a GameCube or Wii disc header magic
    /// (the same check <see cref="RvzWriter.Write(IBlobReader, Stream, RvzWriteOptions, IProgress{double}, CancellationToken)"/> performs before encoding).
    /// </summary>
    /// <param name="reader">The blob to inspect.</param>
    /// <returns>True for a GameCube or Wii disc image; false otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static bool IsDisc(IBlobReader reader)
    {
        return GetDiscType(reader) != DiscType.Unknown;
    }

    /// <summary>Human-readable name of a blob type (Dolphin: GetName).</summary>
    public static string GetName(BlobType type)
    {
        return type switch
        {
            BlobType.Plain => "ISO",
            BlobType.Gcz => "GCZ",
            BlobType.Ciso => "CISO",
            BlobType.Wbfs => "WBFS",
            BlobType.Tgc => "TGC",
            BlobType.Wia => "WIA",
            BlobType.Rvz => "RVZ",
            BlobType.Nfs => "NFS",
            _ => ""
        };
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
                return false;
            }

            total += read;
        }

        return true;
    }
}
