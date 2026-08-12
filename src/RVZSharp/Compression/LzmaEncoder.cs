using SevenZip;

namespace RVZSharp.Compression;

/// <summary>
/// LZMA1 and LZMA2 encoders built on the 7-Zip SDK (LZMA-SDK package). LZMA2 output is
/// produced as a sequence of independent LZMA1 chunks with the properties repeated, which the
/// 7-Zip decoder accepts and our <see cref="LzmaDecoder"/> handles (same as Dolphin's writer).
/// </summary>
public sealed class LzmaEncoder : ICompressionEncoder
{
    private readonly bool _lzma2;
    private readonly int _dictionarySize;

    public LzmaEncoder(bool lzma2, int level = 3)
    {
        _lzma2 = lzma2;
        // Level → dictionary size, following Dolphin's LZMACompressor mapping (bounded).
        _dictionarySize = level switch
        {
            <= 1 => 1 << 18,
            <= 3 => 1 << 20,
            <= 5 => 1 << 23,
            <= 7 => 1 << 25,
            _ => 1 << 27,
        };
    }

    /// <summary>The 7-Zip properties for compr_data (LZMA1: 5 bytes; LZMA2: 1 byte).</summary>
    public byte[] Properties
    {
        get
        {
            if (_lzma2)
            {
                // 7-Zip LZMA2 dict-size byte: 2^(prop/2 + 12) for even props, 2^(prop/2 + 11)
                // for odd ones (matches the reader's table).
                var dictLog2 = 0;
                var size = _dictionarySize;
                while ((1 << dictLog2) < size)
                {
                    dictLog2++;
                }

                var prop = 2 * (dictLog2 - 12) + (dictLog2 % 2 == 0 ? 1 : 0);
                return [(byte)Math.Max(0, prop)];
            }

            var encoder = new SevenZip.Compression.LZMA.Encoder();
            encoder.SetCoderProperties(
                [CoderPropID.DictionarySize, CoderPropID.PosStateBits,
                 CoderPropID.LitContextBits, CoderPropID.LitPosBits],
                [_dictionarySize, 2, 3, 0]);
            using var props = new MemoryStream();
            encoder.WriteCoderProperties(props);
            return props.ToArray();
        }
    }

    public byte[] Compress(ReadOnlySpan<byte> data) => _lzma2 ? EncodeLzma2(data) : EncodeLzma1(data);

    public void AddPrecedingData(ReadOnlySpan<byte> data) { }

    private byte[] EncodeLzma1(ReadOnlySpan<byte> data)
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits,
             CoderPropID.LitContextBits, CoderPropID.LitPosBits],
            [_dictionarySize, 2, 3, 0]);
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var output = new MemoryStream();
        // Size-known stream (no end marker): the readers of both RVZSharp and Dolphin decode
        // with a known size (the group payload length), so the marker is unnecessary.
        encoder.Code(input, output, data.Length, data.Length, null);
        return output.ToArray();
    }

    /// <summary>
    /// Raw LZMA2: 0xF800-byte chunks, each a complete LZMA1 stream (with its own 5-byte
    /// properties) preceded by the 6-byte LZMA2 chunk header (control 0xE0+, unpack size-1,
    /// pack size-1, props byte), ended by a 0x00 control byte.
    /// </summary>
    private byte[] EncodeLzma2(ReadOnlySpan<byte> data)
    {
        const int maxChunk = 0xF800;
        using var output = new MemoryStream();
        var lzmaProps = WriteLzma1Properties();
        for (var offset = 0; offset < data.Length; offset += maxChunk)
        {
            var part = data.Slice(offset, Math.Min(maxChunk, data.Length - offset)).ToArray();
            var lzma1 = EncodeLzma1(part);
            var control = (byte)(0xE0 | ((part.Length - 1) >> 16));
            var header = new[]
            {
                control,
                (byte)((part.Length - 1) >> 8),
                (byte)((part.Length - 1) & 0xFF),
                (byte)((lzma1.Length - 1) >> 8),
                (byte)((lzma1.Length - 1) & 0xFF),
                lzmaProps[0], // lc/lp/pb byte (0x5D for lc=3, lp=0, pb=2)
            };
            output.Write(header);
            output.Write(lzma1);
        }

        output.WriteByte(0); // end marker
        return output.ToArray();
    }

    private byte[] WriteLzma1Properties()
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits,
             CoderPropID.LitContextBits, CoderPropID.LitPosBits],
            [_dictionarySize, 2, 3, 0]);
        using var props = new MemoryStream();
        encoder.WriteCoderProperties(props);
        return props.ToArray();
    }
}
