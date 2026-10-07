namespace RVZSharp.IO;

/// <summary>
/// IEEE CRC-32 (zlib polynomial 0xEDB88320), as used by Dolphin's volume verifier and
/// DolphinTool's verify command.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Feeds <paramref name="data"/> into the running CRC (init 0xFFFFFFFF, xor-out 0xFFFFFFFF).</summary>
    /// <param name="crc">The running CRC value.</param>
    /// <param name="data">The bytes to add.</param>
    /// <returns>The updated CRC value.</returns>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
