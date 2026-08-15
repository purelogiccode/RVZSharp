namespace RVZSharp.Tests.Helpers;

/// <summary>
/// Independent reference implementation of the RVZ padding PRNG, translated literally from
/// Dolphin's LaggedFibonacciGenerator.cpp (including its swap32 pre-transform). Used to
/// cross-validate RVZSharp.Packing.LaggedFibonacciPrng, which follows the format spec instead.
/// </summary>
internal static class ReferencePrng
{
    private const int SeedWords = 17;
    private const int BufferWords = 521;
    private const int J = 32;

    public static byte[] Generate(byte[] seed68, long dataOffset, int count)
    {
        var buffer = new uint[BufferWords];

        // SetSeed: Common::swap32 on the big-endian seed bytes → the word values.
        for (var i = 0; i < SeedWords; i++)
        {
            buffer[i] = (uint)((seed68[i * 4] << 24) | (seed68[i * 4 + 1] << 16) |
                               (seed68[i * 4 + 2] << 8) | seed68[i * 4 + 3]);
        }

        // Initialize fill loop.
        for (var i = SeedWords; i < BufferWords; i++)
        {
            buffer[i] = (buffer[i - 17] << 23) ^ (buffer[i - 16] >> 9) ^ buffer[i - 1];
        }

        // Initialize pre-transform: "instead of doing the shift by 18 instead of 16 oddity when
        // actually outputting the data, we can do the shifting (and byteswapping) at this point".
        for (var i = 0; i < BufferWords; i++)
        {
            buffer[i] = Swap32((buffer[i] & 0xFF00FFFF) | ((buffer[i] >> 2) & 0x00FF0000));
        }

        for (var r = 0; r < 4; r++)
        {
            Forward(buffer);
        }

        var output = new byte[count];
        var bytePosition = 0;
        var outPos = 0;

        // Forward(dataOffset % 0x8000)
        var skip = (int)(dataOffset % 0x8000);
        while (skip > 0)
        {
            var take = Math.Min(skip, BufferWords * 4 - bytePosition);
            bytePosition += take;
            skip -= take;
            if (bytePosition == BufferWords * 4)
            {
                Forward(buffer);
                bytePosition = 0;
            }
        }

        while (outPos < count)
        {
            if (bytePosition == BufferWords * 4)
            {
                Forward(buffer);
                bytePosition = 0;
            }

            var take = Math.Min(count - outPos, BufferWords * 4 - bytePosition);
            for (var i = 0; i < take; i++)
            {
                // GetByte: native (little-endian) byte of the buffer word.
                var word = buffer[(bytePosition + i) >> 2];
                var b = (bytePosition + i) & 3;
                output[outPos + i] = b switch
                {
                    0 => unchecked((byte)word),
                    1 => unchecked((byte)(word >> 8)),
                    2 => unchecked((byte)(word >> 16)),
                    _ => unchecked((byte)(word >> 24))
                };
            }

            bytePosition += take;
            outPos += take;
        }

        return output;
    }

    private static void Forward(uint[] buffer)
    {
        for (var i = 0; i < J; i++)
        {
            buffer[i] ^= buffer[i + BufferWords - J];
        }

        for (var i = J; i < BufferWords; i++)
        {
            buffer[i] ^= buffer[i - J];
        }
    }

    private static uint Swap32(uint value)
    {
        return ((value & 0xFF) << 24) | ((value & 0xFF00) << 8) | ((value >> 8) & 0xFF00) | ((value >> 24) & 0xFF);
    }
}
