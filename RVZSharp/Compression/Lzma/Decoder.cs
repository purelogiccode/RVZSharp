#nullable disable

using RVZSharp.Compression.Lzma.LZ;
using RVZSharp.Interfaces;

namespace RVZSharp.Compression.Lzma;

/// <summary>
/// LZMA decoder core: owns the probability models, the state machine and the output
/// window, and drives the range decoder to produce decompressed bytes. The decode loop
/// itself lives in <c>Decoder.Fast.cs</c> (the only decode path).
/// </summary>
internal partial class Decoder : ISetDecoderProperties
{
    /// <summary>Whether the decoder has reached the end-of-stream marker (match distance 0xFFFFFFFF).</summary>
    internal bool HasEndMarker => _rep0 == uint.MaxValue;

    private Base.State _state;

    private uint _rep0,
        _rep1,
        _rep2,
        _rep3;

    private uint _posStateMask;

    /// <summary>
    /// Runs a buffered decode session against a caller-provided window and range decoder,
    /// producing bytes until the window limit is met or the end-of-stream marker is decoded.
    /// </summary>
    /// <param name="dictionarySize">The dictionary (window) size used to validate match distances.</param>
    /// <param name="outWindow">The output window that receives decoded bytes.</param>
    /// <param name="rangeDecoder">The range decoder consuming the compressed input.</param>
    /// <returns>True when the end-of-stream marker was encountered.</returns>
    internal bool Code(int dictionarySize, OutWindow outWindow, RangeCoder.Decoder rangeDecoder)
    {
        return CodeFast(dictionarySize, outWindow, rangeDecoder);
    }

    /// <summary>
    /// Applies the LZMA properties (lc/lp/pb) and (re)initializes all probability models
    /// so decoding can begin.
    /// </summary>
    /// <param name="properties">1-byte or 5-byte property block from the stream header.</param>
    public void SetDecoderProperties(byte[] properties)
    {
        if (properties.Length < 1)
        {
            throw new InvalidParamException();
        }

        var lc = properties[0] % 9;
        var remainder = properties[0] / 9;
        var lp = remainder % 5;
        var pb = remainder / 5;
        if (pb > Base.K_NUM_POS_STATES_BITS_MAX)
        {
            throw new InvalidParamException();
        }

        _posStateMask = ((uint)1 << pb) - 1;
        CreateFastModel(lp, lc);
        InitFastModel();
    }
}
