namespace RVZSharp.Packing;

/// <summary>
/// The Lagged Fibonacci generator used for Wii disc padding data (Dolphin:
/// LaggedFibonacciGenerator). Parameters: f = xor, j = 32, k = 521. The seed is 17 big-endian
/// u32 words; the generator runs 4 warm-up advances before output, and the output byte order
/// is the "shift by 18 instead of 16" representation of the internal words.
/// </summary>
public sealed class LaggedFibonacciGenerator
{
    public const int BufferWords = 521;
    public const int J = 32;
    public const int SeedWords = 17;
    public const int SeedSize = SeedWords * 4;

    private readonly uint[] _buffer = new uint[BufferWords];
    private int _positionBytes;

    /// <summary>Seeds the generator from the 68-byte big-endian seed.</summary>
    public void SetSeed(ReadOnlySpan<byte> seed)
    {
        _positionBytes = 0;
        for (var i = 0; i < SeedWords; i++)
        {
            _buffer[i] = (uint)((seed[i * 4] << 24) | (seed[i * 4 + 1] << 16) |
                                (seed[i * 4 + 2] << 8) | seed[i * 4 + 3]);
        }

        Initialize(check: false);
    }

    /// <summary>Advances the state by one full buffer (521 words).</summary>
    public void Forward()
    {
        for (var i = 0; i < J; i++)
        {
            _buffer[i] ^= _buffer[i + BufferWords - J];
        }

        for (var i = J; i < BufferWords; i++)
        {
            _buffer[i] ^= _buffer[i - J];
        }
    }

    /// <summary>Advances the state by <paramref name="count"/> bytes of output.</summary>
    public void ForwardBytes(long count)
    {
        // count is long (callers pass up to 0x8000, but the signature must not overflow):
        // accumulate in long and forward whole buffers.
        var position = _positionBytes + count;
        while (position >= BufferWords * 4)
        {
            Forward();
            position -= BufferWords * 4;
        }

        _positionBytes = (int)position;
    }

    /// <summary>Writes <paramref name="count"/> output bytes.</summary>
    public void GetBytes(long count, Span<byte> output)
    {
        var outPos = 0;
        while (count > 0)
        {
            var take = (int)Math.Min(count, BufferWords * 4 - _positionBytes);
            for (var i = 0; i < take; i++)
            {
                output[outPos + i] = (byte)(_buffer[(_positionBytes + i) >> 2] >>
                    (8 * ((_positionBytes + i) & 3)));
            }

            _positionBytes += take;
            count -= take;
            outPos += take;
            if (_positionBytes == BufferWords * 4)
            {
                Forward();
                _positionBytes = 0;
            }
        }
    }

    private byte GetByte()
    {
        var b = (byte)(_buffer[_positionBytes >> 2] >> (8 * (_positionBytes & 3)));
        _positionBytes++;
        if (_positionBytes == BufferWords * 4)
        {
            Forward();
            _positionBytes = 0;
        }

        return b;
    }

    private void Backward(int startWord, int endWord)
    {
        var loopEnd = Math.Max(J, startWord);
        for (var i = Math.Min(endWord, BufferWords); i > loopEnd; i--)
        {
            _buffer[i - 1] ^= _buffer[i - 1 - J];
        }

        for (var i = Math.Min(endWord, J); i > startWord; i--)
        {
            _buffer[i - 1] ^= _buffer[i - 1 + BufferWords - J];
        }
    }

    private void Backward()
    {
        Backward(0, BufferWords);
    }

    private bool Initialize(bool check)
    {
        for (var i = SeedWords; i < BufferWords; i++)
        {
            var calculated = (_buffer[i - 17] << 23) ^ (_buffer[i - 16] >> 9) ^ _buffer[i - 1];
            if (check)
            {
                var actual = (_buffer[i] & 0xFF00FFFF) | (_buffer[i] << 2 & 0x00FC0000);
                if ((calculated & 0xFFFCFFFF) != actual)
                {
                    return false;
                }
            }

            _buffer[i] = calculated;
        }

        for (var i = 0; i < BufferWords; i++)
        {
            _buffer[i] = Swap32((_buffer[i] & 0xFF00FFFF) | ((_buffer[i] >> 2) & 0x00FF0000));
        }

        for (var i = 0; i < 4; i++)
        {
            Forward();
        }

        return true;
    }

    private byte[] Reinitialize()
    {
        for (var i = 0; i < 4; i++)
        {
            Backward();
        }

        for (var i = 0; i < BufferWords; i++)
        {
            _buffer[i] = Swap32(_buffer[i]);
        }

        for (var i = 0; i < SeedWords; i++)
        {
            _buffer[i] = (_buffer[i] & 0xFF00FFFF) | (_buffer[i] << 2 & 0x00FC0000) |
                         ((_buffer[i + 16] ^ _buffer[i + 15]) << 9 & 0x00030000);
        }

        var seed = new byte[SeedSize];
        for (var i = 0; i < SeedWords; i++)
        {
            // The seed is stored as big-endian words: the reader's SetSeed reads them as BE
            // u32s directly (no byte swap).
            var word = _buffer[i];
            seed[i * 4] = (byte)(word >> 24);
            seed[i * 4 + 1] = (byte)(word >> 16);
            seed[i * 4 + 2] = (byte)(word >> 8);
            seed[i * 4 + 3] = (byte)word;
        }

        if (!Initialize(check: true))
        {
            return [];
        }

        return seed;
    }

    /// <summary>
    /// Tries to find a seed that regenerates <paramref name="data"/> (which must be LFG junk
    /// produced at byte position <paramref name="dataOffset"/> modulo 0x8000). Returns the
    /// seed and the number of bytes it reconstructs, or an empty seed when the data does not
    /// look like PRNG junk (Dolphin: LaggedFibonacciGenerator::GetSeed).
    /// </summary>
    public static (byte[] Seed, long BytesReconstructed) GetSeed(
        ReadOnlySpan<byte> data, long size, long dataOffset)
    {
        var result = (Seed: Array.Empty<byte>(), BytesReconstructed: 0L);
        var bytesToSkip = (int)((4 - dataOffset % 4) % 4);
        if (size - bytesToSkip < BufferWords * 4)
        {
            return result;
        }

        var u32Size = (int)((size - bytesToSkip) / 4);
        var u32DataOffset = (dataOffset + bytesToSkip) / 4;
        var modK = (int)(u32DataOffset % BufferWords);
        var divK = u32DataOffset / BufferWords;

        var generator = new LaggedFibonacciGenerator();
        Span<uint> words = stackalloc uint[u32Size];
        for (var i = 0; i < u32Size; i++)
        {
            words[i] = (uint)(data[bytesToSkip + i * 4] | (data[bytesToSkip + i * 4 + 1] << 8) |
                              (data[bytesToSkip + i * 4 + 2] << 16) |
                              (data[bytesToSkip + i * 4 + 3] << 24));
        }

        // Quick check to filter out most data that can't be PRNG junk.
        for (var i = 0; i < BufferWords; i++)
        {
            var x = Swap32(words[i]);
            if ((x & 0x00C00000) != ((x >> 2) & 0x00C00000))
            {
                return result;
            }
        }

        for (var i = 0; i < BufferWords - modK; i++)
        {
            generator._buffer[modK + i] = words[i];
        }

        for (var i = BufferWords - modK; i < BufferWords; i++)
        {
            generator._buffer[i - (BufferWords - modK)] = words[i];
        }

        generator.Backward(0, modK);
        for (var i = 0; i < divK; i++)
        {
            generator.Backward();
        }

        var seed = generator.Reinitialize();
        if (seed.Length == 0)
        {
            return result;
        }

        for (var i = 0; i < divK; i++)
        {
            generator.Forward();
        }

        generator._positionBytes = (int)(dataOffset % (BufferWords * 4));

        long bytesReconstructed = 0;
        while (bytesReconstructed < size && generator.GetByte() == data[(int)bytesReconstructed])
        {
            bytesReconstructed++;
        }

        return (seed, bytesReconstructed);
    }

    private static uint Swap32(uint value)
    {
        return ((value & 0xFF) << 24) | ((value & 0xFF00) << 8) | ((value >> 8) & 0xFF00) | ((value >> 24) & 0xFF);
    }
}
