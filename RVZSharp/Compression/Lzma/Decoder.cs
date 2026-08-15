#nullable disable

using System.Diagnostics.CodeAnalysis;
using RVZSharp.Compression.Lzma.LZ;
using RVZSharp.Compression.Lzma.RangeCoder;
using RVZSharp.Interfaces;

namespace RVZSharp.Compression.Lzma;

/// <summary>
/// LZMA decoder core: owns the probability models, the state machine and the output
/// window, and drives the range decoder to produce decompressed bytes.
/// </summary>
internal partial class Decoder : ICoder, ISetDecoderProperties, IDisposable
{
    /// <summary>Whether the decoder has reached the end-of-stream marker (match distance 0xFFFFFFFF).</summary>
    internal bool HasEndMarker => _rep0 == uint.MaxValue;

    /// <summary>Releases the internal output window buffer.</summary>
    public void Dispose()
    {
        _outWindow?.Dispose();
        _outWindow = null;
    }

    private class LenDecoder
    {
        private BitDecoder _choice;
        private BitDecoder _choice2;
        private readonly BitTreeDecoder[] _lowCoder = new BitTreeDecoder[Base.K_NUM_POS_STATES_MAX];
        private readonly BitTreeDecoder[] _midCoder = new BitTreeDecoder[Base.K_NUM_POS_STATES_MAX];
        private readonly BitTreeDecoder _highCoder = new(Base.K_NUM_HIGH_LEN_BITS);
        private uint _numPosStates;

        public void Create(uint numPosStates)
        {
            for (var posState = _numPosStates; posState < numPosStates; posState++)
            {
                _lowCoder[posState] = new BitTreeDecoder(Base.K_NUM_LOW_LEN_BITS);
                _midCoder[posState] = new BitTreeDecoder(Base.K_NUM_MID_LEN_BITS);
            }

            _numPosStates = numPosStates;
        }

        public void Init()
        {
            _choice.Init();
            for (uint posState = 0; posState < _numPosStates; posState++)
            {
                _lowCoder[posState].Init();
                _midCoder[posState].Init();
            }

            _choice2.Init();
            _highCoder.Init();
        }
    }

    private class LiteralDecoder
    {
        private struct Decoder2
        {
            private BitDecoder[] _decoders;
            private int _baseIndex;

            public void Create(BitDecoder[] decoders, int baseIndex)
            {
                _decoders = decoders;
                _baseIndex = baseIndex;
            }

            public readonly void Init()
            {
                for (var i = 0; i < 0x300; i++)
                {
                    _decoders[_baseIndex + i].Init();
                }
            }
        }

        private Decoder2[] _coders;
        private BitDecoder[] _models;
        private int _numPrevBits;
        private int _numPosBits;
        private uint _posMask;

        public void Create(int numPosBits, int numPrevBits)
        {
            if (_coders != null && _numPrevBits == numPrevBits && _numPosBits == numPosBits)
            {
                return;
            }

            _numPosBits = numPosBits;
            _posMask = ((uint)1 << numPosBits) - 1;
            _numPrevBits = numPrevBits;
            var numStates = (uint)1 << (_numPrevBits + _numPosBits);
            _models = new BitDecoder[checked((int)(numStates * 0x300))];
            _coders = new Decoder2[numStates];
            for (uint i = 0; i < numStates; i++)
            {
                _coders[i].Create(_models, checked((int)(i * 0x300)));
            }
        }

        public void Init()
        {
            var numStates = (uint)1 << (_numPrevBits + _numPosBits);
            for (uint i = 0; i < numStates; i++)
            {
                _coders[i].Init();
            }
        }
    }

    private OutWindow _outWindow;

    private readonly BitDecoder[] _isMatchDecoders = new BitDecoder[
        Base.K_NUM_STATES << Base.K_NUM_POS_STATES_BITS_MAX
    ];

    private readonly BitDecoder[] _isRepDecoders = new BitDecoder[Base.K_NUM_STATES];
    private readonly BitDecoder[] _isRepG0Decoders = new BitDecoder[Base.K_NUM_STATES];
    private readonly BitDecoder[] _isRepG1Decoders = new BitDecoder[Base.K_NUM_STATES];
    private readonly BitDecoder[] _isRepG2Decoders = new BitDecoder[Base.K_NUM_STATES];

    private readonly BitDecoder[] _isRep0LongDecoders = new BitDecoder[
        Base.K_NUM_STATES << Base.K_NUM_POS_STATES_BITS_MAX
    ];

    private readonly BitTreeDecoder[] _posSlotDecoder = new BitTreeDecoder[
        Base.K_NUM_LEN_TO_POS_STATES
    ];

    private readonly BitDecoder[] _posDecoders = new BitDecoder[
        Base.K_NUM_FULL_DISTANCES - Base.K_END_POS_MODEL_INDEX
    ];

    private readonly BitTreeDecoder _posAlignDecoder = new(Base.K_NUM_ALIGN_BITS);

    private readonly LenDecoder _lenDecoder = new();
    private readonly LenDecoder _repLenDecoder = new();

    private readonly LiteralDecoder _literalDecoder = new();

    private int _dictionarySize;

    private uint _posStateMask;

    private Base.State _state;

    private uint _rep0,
        _rep1,
        _rep2,
        _rep3;

    /// <summary>Creates a decoder; the models and properties are initialized by a later SetDecoderProperties call.</summary>
    public Decoder()
    {
        _dictionarySize = -1;
        for (var i = 0; i < Base.K_NUM_LEN_TO_POS_STATES; i++)
        {
            _posSlotDecoder[i] = new BitTreeDecoder(Base.K_NUM_POS_SLOT_BITS);
        }
    }

    [MemberNotNull(nameof(_outWindow))]
    private void CreateDictionary()
    {
        if (_dictionarySize < 0)
        {
            throw new InvalidParamException();
        }

        _outWindow = new OutWindow();
        var blockSize = Math.Max(_dictionarySize, (1 << 12));
        _outWindow.Create(blockSize);
    }

    private void SetLiteralProperties(int lp, int lc)
    {
        if (lp > 8 || lc > 8)
        {
            throw new InvalidParamException();
        }

        _literalDecoder.Create(lp, lc);
    }

    private void SetPosBitsProperties(int pb)
    {
        if (pb > Base.K_NUM_POS_STATES_BITS_MAX)
        {
            throw new InvalidParamException();
        }

        var numPosStates = (uint)1 << pb;
        _lenDecoder.Create(numPosStates);
        _repLenDecoder.Create(numPosStates);
        _posStateMask = numPosStates - 1;
    }

    private void Init()
    {
        uint i;
        for (i = 0; i < Base.K_NUM_STATES; i++)
        {
            for (uint j = 0; j <= _posStateMask; j++)
            {
                var index = (i << Base.K_NUM_POS_STATES_BITS_MAX) + j;
                _isMatchDecoders[index].Init();
                _isRep0LongDecoders[index].Init();
            }

            _isRepDecoders[i].Init();
            _isRepG0Decoders[i].Init();
            _isRepG1Decoders[i].Init();
            _isRepG2Decoders[i].Init();
        }

        _literalDecoder.Init();
        for (i = 0; i < Base.K_NUM_LEN_TO_POS_STATES; i++)
        {
            _posSlotDecoder[i].Init();
        }

        // _PosSpecDecoder.Init();
        for (i = 0; i < Base.K_NUM_FULL_DISTANCES - Base.K_END_POS_MODEL_INDEX; i++)
        {
            _posDecoders[i].Init();
        }

        _lenDecoder.Init();
        _repLenDecoder.Init();
        _posAlignDecoder.Init();

        _state.Init();
        _rep0 = 0;
        _rep1 = 0;
        _rep2 = 0;
        _rep3 = 0;
    }

    /// <summary>
    /// Decodes a whole stream: allocates the output window and a fresh range decoder, decodes
    /// with its own Code overload and flushes the output window to the destination stream.
    /// </summary>
    /// <param name="inStream">The compressed input stream.</param>
    /// <param name="outStream">The stream receiving the decompressed bytes.</param>
    /// <param name="inSize">Exact compressed size, or -1 if unknown.</param>
    /// <param name="outSize">Expected decompressed size, or -1 if unknown.</param>
    /// <param name="progress">Optional progress callback (unused by this port).</param>
    public void Code(
        Stream inStream,
        Stream outStream,
        long inSize,
        long outSize,
        ICodeProgress progress
    )
    {
        if (_outWindow is null)
        {
            CreateDictionary();
        }

        _outWindow.Init(outStream);
        if (outSize > 0)
        {
            _outWindow.SetLimit(outSize);
        }
        else
        {
            _outWindow.SetLimit(long.MaxValue - _outWindow.Total);
        }

        var rangeDecoder = new RangeCoder.Decoder();
        rangeDecoder.Init(inStream);

        Code(_dictionarySize, _outWindow, rangeDecoder);

        _outWindow.ReleaseStream();
        rangeDecoder.ReleaseStream();

        _outWindow.Dispose();
        _outWindow = null;
    }

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
    /// Applies the LZMA properties (lc/lp/pb and, when present, the 4-byte dictionary size)
    /// and (re)initializes all probability models so decoding can begin.
    /// </summary>
    /// <param name="properties">1-byte or 5-byte property block from the stream header.</param>
    public void SetDecoderProperties(byte[] properties)
    {
        SetDecoderProperties(properties.AsSpan());
    }

    /// <summary>Span-based variant of SetDecoderProperties.</summary>
    /// <param name="properties">The property bytes; the first byte holds lc/lp/pb, bytes 1-4 the dictionary size.</param>
    internal void SetDecoderProperties(ReadOnlySpan<byte> properties)
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

        SetLiteralProperties(lp, lc);
        SetPosBitsProperties(pb);
        Init();
        CreateFastModel(lp, lc);
        InitFastModel();
        if (properties.Length >= 5)
        {
            _dictionarySize = 0;
            for (var i = 0; i < 4; i++)
            {
                _dictionarySize += properties[1 + i] << (i * 8);
            }
        }
    }

    /// <summary>Loads the trailing bytes of a stream into the window as a preset dictionary.</summary>
    /// <param name="stream">The stream whose final bytes become the preset dictionary.</param>
    public void Train(Stream stream)
    {
        if (_outWindow is null)
        {
            CreateDictionary();
        }

        _outWindow.Train(stream);
    }
}
