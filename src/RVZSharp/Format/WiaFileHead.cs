using System.Security.Cryptography;
using RVZSharp.IO;

namespace RVZSharp.Format;

/// <summary>
/// The <c>wia_file_head_t</c> struct, stored at offset 0x0 of a WIA/RVZ file. Exactly
/// <see cref="Size"/> (0x48) bytes long; the layout "will never be changed" per the wit source.
/// All integers are big endian.
/// </summary>
public readonly struct WiaFileHead
{
    public const int Size = 0x48;

    /// <summary>Size of a SHA-1 hash in bytes.</summary>
    public const int HashSize = 20;

    /// <summary>Offset of the <c>file_head_hash</c> field; the hash covers everything before it.</summary>
    public const int FileHeadHashOffset = Size - HashSize; // 0x34

    public static ReadOnlySpan<byte> RvzMagic => "RVZ\x01"u8;
    public static ReadOnlySpan<byte> WiaMagic => "WIA\x01"u8;

    /// <summary>RVZ format version this library implements (Dolphin: RVZ_VERSION).</summary>
    public const uint RvzVersion = 0x01000000;

    /// <summary>Lowest file version this library can read (Dolphin: RVZ_VERSION_READ_COMPATIBLE).</summary>
    public const uint RvzVersionReadCompatible = 0x00030000;

    /// <summary>The 4 magic bytes ("RVZ\x01" for RVZ, "WIA\x01" for WIA).</summary>
    public byte[] Magic { get; }

    public uint Version { get; }
    public uint VersionCompatible { get; }

    /// <summary>Size of the <c>wia_disc_t</c> struct that follows this header.</summary>
    public uint DiscSize { get; }

    /// <summary>SHA-1 of the disc struct (DiscSize bytes).</summary>
    public byte[] DiscHash { get; }

    /// <summary>Size of the original disc image (the ISO this file decodes to).</summary>
    public ulong IsoFileSize { get; }

    /// <summary>Size of this file.</summary>
    public ulong RvzFileSize { get; }

    /// <summary>SHA-1 of this struct up to (but not including) this field.</summary>
    public byte[] FileHeadHash { get; }

    public bool IsRvz => Magic.AsSpan().SequenceEqual(RvzMagic);
    public bool IsWia => Magic.AsSpan().SequenceEqual(WiaMagic);

    private WiaFileHead(
        byte[] magic, uint version, uint versionCompatible, uint discSize, byte[] discHash,
        ulong isoFileSize, ulong rvzFileSize, byte[] fileHeadHash)
    {
        Magic = magic;
        Version = version;
        VersionCompatible = versionCompatible;
        DiscSize = discSize;
        DiscHash = discHash;
        IsoFileSize = isoFileSize;
        RvzFileSize = rvzFileSize;
        FileHeadHash = fileHeadHash;
    }

    /// <summary>Decodes the file head from the first <see cref="Size"/> bytes of the file.</summary>
    /// <exception cref="RvzFormatException">The input is shorter than <see cref="Size"/> bytes.</exception>
    public static WiaFileHead Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new RvzFormatException(
                $"File head needs {Size} bytes, only {data.Length} available.");
        }

        var reader = new SpanReader(data);
        var magic = reader.ReadBytes(4).ToArray();
        var version = reader.ReadUInt32();
        var versionCompatible = reader.ReadUInt32();
        var discSize = reader.ReadUInt32();
        var discHash = reader.ReadBytes(HashSize).ToArray();
        var isoFileSize = reader.ReadUInt64();
        var rvzFileSize = reader.ReadUInt64();
        var fileHeadHash = reader.ReadBytes(HashSize).ToArray();

        return new WiaFileHead(magic, version, versionCompatible, discSize, discHash,
            isoFileSize, rvzFileSize, fileHeadHash);
    }

    /// <summary>
    /// Validates magic, version compatibility, declared file size and the file-head SHA-1.
    /// </summary>
    /// <param name="rawHeader">The first <see cref="Size"/> bytes of the file (must match this struct).</param>
    /// <param name="actualFileSize">Length of the underlying stream.</param>
    /// <exception cref="RvzFormatException">Bad magic or file size mismatch.</exception>
    /// <exception cref="RvzUnsupportedException">WIA files or unsupported versions.</exception>
    /// <exception cref="RvzHashMismatchException">The file head hash does not match.</exception>
    public void Validate(ReadOnlySpan<byte> rawHeader, long actualFileSize)
    {
        if (!IsRvz)
        {
            if (IsWia)
            {
                throw new RvzUnsupportedException(
                    "This file uses the WIA format, which RVZSharp does not support yet.");
            }

            throw new RvzFormatException($"Bad magic: expected \"RVZ\x01\", got "
                + $"0x{Convert.ToHexString(Magic)}.");
        }

        if (Version < RvzVersionReadCompatible)
        {
            throw new RvzUnsupportedException(
                $"RVZ version {FormatVersion(Version)} is too old; this library requires "
                + $"at least {FormatVersion(RvzVersionReadCompatible)}.");
        }

        if (RvzVersion < VersionCompatible)
        {
            throw new RvzUnsupportedException(
                $"RVZ version {FormatVersion(Version)} is too new for this library "
                + $"(compatible from {FormatVersion(RvzVersion)}).");
        }

        if ((long)RvzFileSize != actualFileSize)
        {
            throw new RvzFormatException(
                $"File size mismatch: header declares {RvzFileSize} bytes, actual "
                + $"{actualFileSize} bytes.");
        }

        var actualHash = SHA1.HashData(rawHeader[..FileHeadHashOffset]);
        if (!actualHash.AsSpan().SequenceEqual(FileHeadHash))
        {
            throw new RvzHashMismatchException("The file head SHA-1 does not match its contents.");
        }
    }

    /// <summary>Formats a version like Dolphin does: 0x01000000 → "1.00".</summary>
    public static string FormatVersion(uint version) =>
        $"{(version >> 24)}.{((version >> 16) & 0xff):x2}";
}
