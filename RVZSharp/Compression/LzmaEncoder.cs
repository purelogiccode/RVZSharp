using SevenZip;
using RVZSharp.Interfaces;

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
        // Level → dictionary size, exactly like liblzma's lzma_lzma_preset used by Dolphin
        // (WIACompression.cpp:618): 0→64 KiB, 1→1 MiB, 2→2 MiB, 3→4 MiB, 4→4 MiB, 5→8 MiB,
        // 6→8 MiB, 7→16 MiB, 8→32 MiB, 9→64 MiB.
        _dictionarySize = DictionarySizeForLevel(level);
    }

    private static int DictionarySizeForLevel(int level)
    {
        return level switch
        {
            <= 0 => 1 << 16,
            1 => 1 << 20,
            2 => 1 << 21,
            3 or 4 => 1 << 22,
            5 or 6 => 1 << 23,
            7 => 1 << 24,
            8 => 1 << 25,
            _ => 1 << 26 // 9
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

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        return _lzma2 ? EncodeLzma2(data) : EncodeLzma1(data);
    }

    public void AddPrecedingData(ReadOnlySpan<byte> data) { }

    private byte[] EncodeLzma1(ReadOnlySpan<byte> data)
    {
        // Top-level LZMA1 streams must end with the end-of-stream marker: Dolphin's decoder
        // only reports success on LZMA_STREAM_END (WIACompression.cpp: LZMADecompressor), and
        // our own LzmaStream decodes group chunks with an unknown output size
        // (ChunkDecoder.OpenDecompressor passes -1), so it can only terminate at the marker.
        return EncodeLzma1Core(data, withEndMarker: true);
    }

    private byte[] EncodeLzma1Core(ReadOnlySpan<byte> data, bool withEndMarker)
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        var propIds = new List<CoderPropID>
        {
            CoderPropID.DictionarySize, CoderPropID.PosStateBits,
            CoderPropID.LitContextBits, CoderPropID.LitPosBits
        };
        var propValues = new List<object> { _dictionarySize, 2, 3, 0 };
        if (withEndMarker)
        {
            // The LZMA-SDK package ignores the outSize argument; the end-of-stream marker is
            // controlled exclusively by this property (decompiled: SetStreams never sets
            // _writeEndMark; only CoderPropID.EndMarker reaches SetWriteEndMarkerMode).
            propIds.Add(CoderPropID.EndMarker);
            propValues.Add(true);
        }

        encoder.SetCoderProperties(propIds.ToArray(), propValues.ToArray());
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var output = new MemoryStream();
        // outSize < 0 additionally requests the marker in SDKs that honor it (the official
        // 7-Zip SDK); with the LZMA-SDK package the property above is what takes effect.
        encoder.Code(input, output, data.Length, withEndMarker ? -1 : data.Length, null);
        return output.ToArray();
    }

    /// <summary>
    /// Raw LZMA2: 0xF800-byte chunks, each a complete LZMA1 stream (with its own 5-byte
    /// properties) preceded by the 6-byte LZMA2 chunk header (control 0xE0+, unpack size-1,
    /// pack size-1, props byte), ended by a 0x00 control byte. The inner LZMA1 chunks are
    /// size-terminated (no end marker): the LZMA2 framing carries the pack size, and Dolphin's
    /// decoder consumes exactly that many bytes.
    /// </summary>
    private byte[] EncodeLzma2(ReadOnlySpan<byte> data)
    {
        const int maxChunk = 0xF800;
        using var output = new MemoryStream();
        var lzmaProps = WriteLzma1Properties();
        for (var offset = 0; offset < data.Length; offset += maxChunk)
        {
            var part = data.Slice(offset, Math.Min(maxChunk, data.Length - offset)).ToArray();
            var lzma1 = EncodeLzma1Core(part, withEndMarker: false);
            var control = (byte)(0xE0 | ((part.Length - 1) >> 16));
            var header = new[]
            {
                control,
                (byte)((part.Length - 1) >> 8),
                (byte)((part.Length - 1) & 0xFF),
                (byte)((lzma1.Length - 1) >> 8),
                (byte)((lzma1.Length - 1) & 0xFF),
                lzmaProps[0] // lc/lp/pb byte (0x5D for lc=3, lp=0, pb=2)
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
