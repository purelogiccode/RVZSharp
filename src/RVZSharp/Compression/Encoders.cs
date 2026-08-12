using System.IO.Compression;
using ZstdSharp;

namespace RVZSharp.Compression;

/// <summary>Stores the data unchanged.</summary>
public sealed class NoneEncoder : ICompressionEncoder
{
    public static NoneEncoder Instance { get; } = new();

    public byte[] Compress(ReadOnlySpan<byte> data) => data.ToArray();
    public void AddPrecedingData(ReadOnlySpan<byte> data) { }
}

/// <summary>Zstandard (RFC 8878), like Dolphin's ZstdCompressor (ZSTD_compress).</summary>
public sealed class ZstdEncoder : ICompressionEncoder
{
    private readonly int _level;

    public ZstdEncoder(int level) => _level = Math.Clamp(level, 1, 22);

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, _level, leaveOpen: true))
        {
            zstd.Write(data);
        }

        return output.ToArray();
    }

    public void AddPrecedingData(ReadOnlySpan<byte> data) { }
}

/// <summary>BZip2, like Dolphin's Bzip2Compressor (BZ2_bzCompressInit(level, 0, 0)).</summary>
public sealed class Bzip2Encoder : ICompressionEncoder
{
    private readonly int _level;

    public Bzip2Encoder(int level) => _level = Math.Clamp(level, 1, 9);

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream(output, _level)
               { IsStreamOwner = false })
        {
            bzip2.Write(data);
        }

        return output.ToArray();
    }

    public void AddPrecedingData(ReadOnlySpan<byte> data) { }
}
