using System.Security.Cryptography;
using RVZSharp.Models;

namespace RVZSharp.Tests.Helpers;

/// <summary>Builds a structurally valid RVZ file head (0x48 bytes) for tests.</summary>
public sealed class TestHeaderBuilder
{
    public byte[] Magic { get; set; } = WiaFileHead.RvzMagic.ToArray();
    public uint Version { get; set; } = WiaFileHead.ImplementedVersion;
    public uint VersionCompatible { get; set; } = WiaFileHead.RvzVersionReadCompatible;
    public uint DiscSize { get; set; } = 0xDC;
    public byte[] DiscHash { get; set; } = new byte[WiaFileHead.HashSize];
    public ulong IsoFileSize { get; set; } = 0x1_0000_0000; // 4 GiB disc
    public ulong RvzFileSize { get; set; } = WiaFileHead.Size;

    /// <summary>Writes the header (recomputing the file head hash) into a 0x48-byte buffer.</summary>
    public byte[] Build()
    {
        var b = new byte[WiaFileHead.Size];
        Magic.CopyTo(b, 0);
        WriteBe(b, 4, Version);
        WriteBe(b, 8, VersionCompatible);
        WriteBe(b, 12, DiscSize);
        DiscHash.CopyTo(b, 16);
        WriteBe(b, 36, IsoFileSize);
        WriteBe(b, 44, RvzFileSize);
        var hash = SHA1.HashData(b.AsSpan(0, WiaFileHead.FileHeadHashOffset));
        hash.CopyTo(b, WiaFileHead.FileHeadHashOffset);
        return b;
    }

    private static void WriteBe(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static void WriteBe(byte[] b, int offset, ulong value)
    {
        WriteBe(b, offset, (uint)(value >> 32));
        WriteBe(b, offset + 4, (uint)value);
    }
}
