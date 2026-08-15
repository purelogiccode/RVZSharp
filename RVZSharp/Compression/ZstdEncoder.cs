using ZstdSharp;
using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>Zstandard (RFC 8878), like Dolphin's ZstdCompressor (ZSTD_compress).</summary>
public sealed class ZstdEncoder : ICompressionEncoder
{
    private readonly int _level;

    // Dolphin's CLI accepts ZSTD_minCLevel()..ZSTD_maxCLevel(), i.e. -131072..22
    // (negative levels select fast modes; 0 means the default level).
    public ZstdEncoder(int level)
    {
        _level = Math.Clamp(level, -131072, 22);
    }

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, _level, leaveOpen: true))
        {
            zstd.Write(data);
        }
        return output.ToArray();
    }

    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}