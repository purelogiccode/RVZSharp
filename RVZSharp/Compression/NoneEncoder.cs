using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>Stores the data unchanged.</summary>
public sealed class NoneEncoder : ICompressionEncoder
{
    public static NoneEncoder Instance { get; } = new();

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        return data.ToArray();
    }

    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}