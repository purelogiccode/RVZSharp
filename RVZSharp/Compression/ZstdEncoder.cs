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
        // One-shot ZSTD_compress2 with the source size known up front, like Dolphin's
        // ZstdCompressor (ZSTD_compress). A streaming session without a pledged source size
        // sizes its context from the level's window log, so level 22 reserves ~128 MiB of
        // window per worker before it sees any input; compressing groups in parallel then
        // runs out of memory on machines with modest RAM. One-shot sizing caps the window at
        // the chunk size instead, which is all a single frame can reference anyway.
        using var compressor = new Compressor(_level);
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>No-op: Zstandard compression has no preceding data to cover.</summary>
    /// <param name="data">Ignored.</param>
    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}
