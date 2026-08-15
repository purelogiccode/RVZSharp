namespace RVZSharp.Interfaces;

/// <summary>
/// Compresses one group chunk (or table) with one of the RVZ/WIA compression methods.
/// Implementations mirror Dolphin's WIACompression.cpp compressors.
/// </summary>
public interface ICompressionEncoder
{
    /// <summary>Compresses <paramref name="data"/> and returns the stored bytes.</summary>
    /// <param name="data">The uncompressed group chunk bytes to store.</param>
    /// <returns>The stored (compressed) bytes.</returns>
    byte[] Compress(ReadOnlySpan<byte> data);

    /// <summary>
    /// For PURGE: bytes that precede the compressed stream and must be covered by the
    /// SHA-1 trailer (the exception lists). Empty for every other method.
    /// </summary>
    /// <param name="data">The bytes preceding the compressed stream.</param>
    void AddPrecedingData(ReadOnlySpan<byte> data);
}
