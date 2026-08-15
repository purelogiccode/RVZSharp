using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>BZip2, like Dolphin's Bzip2Compressor (BZ2_bzCompressInit(level, 0, 0)).</summary>
public sealed class Bzip2Encoder : ICompressionEncoder
{
    private readonly int _level;

    public Bzip2Encoder(int level)
    {
        _level = Math.Clamp(level, 1, 9);
    }

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

    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}