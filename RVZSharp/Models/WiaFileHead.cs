using System.Security.Cryptography;
using RVZSharp.IO;

namespace RVZSharp.Models;

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

    /// <summary>Version this library implements for both formats (Dolphin: RVZ_VERSION / WIA_VERSION).</summary>
    public const uint ImplementedVersion = 0x01000000;

    /// <summary>Lowest file version this library can read (Dolphin: RVZ_VERSION_READ_COMPATIBLE).</summary>
    public const uint RvzVersionReadCompatible = 0x00030000;

    /// <summary>Lowest file version this library can read (Dolphin: WIA_VERSION_READ_COMPATIBLE).</summary>
    public const uint WiaVersionReadCompatible = 0x00080000;

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

    /// <summary>Validates this header as an RVZ file head.</summary>
    public void Validate(ReadOnlySpan<byte> rawHeader, long actualFileSize)
    {
        Validate(rawHeader, actualFileSize, WiaRvzFormat.Rvz);
    }

    /// <summary>
    /// Validates magic, version compatibility, declared file size and the file-head SHA-1.
    /// </summary>
    /// <param name="rawHeader">The first <see cref="Size"/> bytes of the file (must match this struct).</param>
    /// <param name="actualFileSize">Length of the underlying stream.</param>
    /// <param name="format">Which format the magic and version rules belong to.</param>
    /// <exception cref="RvzFormatException">Bad magic or file size mismatch.</exception>
    /// <exception cref="RvzUnsupportedException">Unsupported versions.</exception>
    /// <exception cref="RvzHashMismatchException">The file head hash does not match.</exception>
    public void Validate(ReadOnlySpan<byte> rawHeader, long actualFileSize, WiaRvzFormat format)
    {
        var expectedMagic = format == WiaRvzFormat.Wia ? WiaMagic : RvzMagic;
        if (!Magic.AsSpan().SequenceEqual(expectedMagic))
        {
            throw new RvzFormatException(
                $"Bad magic: expected \"{System.Text.Encoding.ASCII.GetString(expectedMagic)}\", got "
                + $"0x{Convert.ToHexString(Magic)}.");
        }

        var formatName = format == WiaRvzFormat.Wia ? "WIA" : "RVZ";
        var readCompatible =
            format == WiaRvzFormat.Wia ? WiaVersionReadCompatible : RvzVersionReadCompatible;

        if (Version < readCompatible)
        {
            throw new RvzUnsupportedException(
                $"{formatName} version {FormatVersion(Version)} is too old; this library requires "
                + $"at least {FormatVersion(readCompatible)}.");
        }

        if (ImplementedVersion < VersionCompatible)
        {
            throw new RvzUnsupportedException(
                $"{formatName} version {FormatVersion(Version)} is too new for this library "
                + $"(compatible from {FormatVersion(ImplementedVersion)}).");
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

    /// <summary>
    /// Formats a version like Dolphin's VersionToString (WIABlob.cpp:618-629):
    /// major.minor.revision, plus a .beta suffix when the fourth byte is neither 0 nor 0xff.
    /// </summary>
    public static string FormatVersion(uint version)
    {
        var a = version >> 24;
        var b = (version >> 16) & 0xff;
        var c = (version >> 8) & 0xff;
        var d = version & 0xff;
        return d is 0 or 0xff
            ? $"{a}.{b:x2}.{c:x2}"
            : $"{a}.{b:x2}.{c:x2}.beta{d}";
    }
}
