namespace RVZSharp.IO;

/// <summary>The zlib Adler-32 checksum (used by the GCZ format to verify stored blocks).</summary>
public static class Adler32
{
    private const uint ModAdler = 65521;

    /// <summary>Computes the Adler-32 checksum of the given bytes.</summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The Adler-32 checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % ModAdler;
            b = (b + a) % ModAdler;
        }

        return (b << 16) | a;
    }
}
