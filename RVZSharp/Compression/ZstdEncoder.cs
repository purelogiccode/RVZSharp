using ZstdSharp;
using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>Zstandard (RFC 8878), like Dolphin's ZstdCompressor (ZSTD_compress).</summary>
public sealed class ZstdEncoder : ICompressionEncoder
{
    private readonly int _level;

    // Dolphin's CLI accepts ZSTD_minCLevel()..ZSTD_maxCLevel(), i.e. -131072..22
    // (negative levels select fast modes; 0 means the default level).
    /// <summary>
    /// Creates an encoder with the given Zstandard level, clamped to ZSTD_minCLevel()..
    /// ZSTD_maxCLevel(), i.e. -131072..22 (negative levels select fast modes; 0 means the
    /// default level), matching Dolphin's CLI.
    /// </summary>
    /// <param name="level">Zstandard compression level.</param>
    public ZstdEncoder(int level)
    {
        _level = Math.Clamp(level, -131072, 22);
    }

    /// <summary>Compresses <c>data</c> into a Zstandard (RFC 8878) frame.</summary>
    /// <param name="data">The data to compress.</param>
    /// <returns>The Zstandard-compressed bytes.</returns>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, _level, leaveOpen: true))
        {
            zstd.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>No-op: Zstandard compression has no preceding data to cover.</summary>
    /// <param name="data">Ignored.</param>
    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}