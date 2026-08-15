using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>BZip2, like Dolphin's Bzip2Compressor (BZ2_bzCompressInit(level, 0, 0)).</summary>
public sealed class Bzip2Encoder : ICompressionEncoder
{
    private readonly int _level;

    /// <summary>
    /// Creates an encoder with the given bzip2 block size level (clamped to 1-9, the valid
    /// blockSize100k range for BZ2_bzCompressInit).
    /// </summary>
    /// <param name="level">bzip2 block size level, 1 (fast/small) to 9 (slow/large).</param>
    public Bzip2Encoder(int level)
    {
        _level = Math.Clamp(level, 1, 9);
    }

    /// <summary>Compresses <c>data</c> into a bzip2 stream.</summary>
    /// <param name="data">The data to compress.</param>
    /// <returns>The bzip2-compressed bytes.</returns>
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

    /// <summary>No-op: bzip2 compression has no preceding data to cover.</summary>
    /// <param name="data">Ignored.</param>
    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}