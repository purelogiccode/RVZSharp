using RVZSharp.Interfaces;

namespace RVZSharp.Compression;

/// <summary>Stores the data unchanged.</summary>
public sealed class NoneEncoder : ICompressionEncoder
{
    /// <summary>The singleton NONE encoder instance.</summary>
    public static NoneEncoder Instance { get; } = new();

    /// <summary>Returns a copy of the input unchanged.</summary>
    /// <param name="data">The data to store.</param>
    /// <returns>The data unchanged, as a new byte array.</returns>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        return data.ToArray();
    }

    /// <summary>No-op: NONE compression has no preceding data to cover.</summary>
    /// <param name="data">Ignored.</param>
    public void AddPrecedingData(ReadOnlySpan<byte> data)
    {
    }
}