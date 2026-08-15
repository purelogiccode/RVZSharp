// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// Trimmed to the LZMA1/LZMA2 *decode* paths only; namespace renamed to RVZSharp.Compression.Lzma.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

using System.Buffers.Binary;
using RVZSharp.Compression.Lzma.LZ;

namespace RVZSharp.Compression.Lzma;

/// <summary>
/// Streaming LZMA1 (5-byte 7-Zip properties) or LZMA2 (1-byte dictionary-size property) decoder.
/// Supports unknown output size: LZMA1 streams must be terminated by an end-of-stream marker,
/// LZMA2 streams by the 0x00 control byte (both are what RVZ/Dolphin writers produce).
/// </summary>
public sealed class LzmaStream : Stream
{
    private readonly Stream? _inputStream;
    private readonly long _inputSize;
    private readonly long _outputSize;
    private readonly bool _leaveOpen;

    private readonly int _dictionarySize;
    private readonly OutWindow _outWindow = new();
    private readonly RangeCoder.Decoder _rangeDecoder = new();
    private Decoder? _decoder;

    private long _position;
    private bool _endReached;
    private long _availableBytes;
    private long _rangeDecoderLimit;

    // LZMA2
    private readonly bool _isLzma2;
    private bool _uncompressedChunk;
    private bool _needDictReset = true;
    private bool _needProps = true;

    private bool _isDisposed;

    private LzmaStream(
        byte[] properties,
        Stream inputStream,
        long inputSize,
        long outputSize,
        bool isLzma2,
        bool leaveOpen = false
    )
    {
        _inputStream = inputStream;
        _inputSize = inputSize;
        _outputSize = outputSize;
        _isLzma2 = isLzma2;
        _leaveOpen = leaveOpen;
        if (!isLzma2)
        {
            _dictionarySize = BinaryPrimitives.ReadInt32LittleEndian(properties.AsSpan(1));
            _outWindow.Create(_dictionarySize);

            _decoder = new Decoder();
            _decoder.SetDecoderProperties(properties);
            Properties = properties;

            _availableBytes = outputSize < 0 ? long.MaxValue : outputSize;
            _rangeDecoderLimit = inputSize;
        }
        else
        {
            _dictionarySize = 2 | (properties[0] & 1);
            _dictionarySize <<= (properties[0] >> 1) + 11;

            _outWindow.Create(_dictionarySize);

            Properties = new byte[1];
            _availableBytes = 0;
        }
    }

    /// <summary>
    /// Creates a decoder for a raw LZMA1 stream (when <paramref name="properties"/> has 5 bytes)
    /// or a raw LZMA2 stream (when it has 1 byte), matching the property formats used by RVZ.
    /// </summary>
    /// <param name="properties">The compressor properties from the RVZ disc header.</param>
    /// <param name="inputStream">Stream of the compressed data.</param>
    /// <param name="inputSize">Exact compressed size, or -1 if unknown.</param>
    /// <param name="outputSize">Expected decompressed size, or -1 if unknown.</param>
    /// <param name="presetDictionary">Optional preset dictionary (not used by RVZ).</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inputStream"/> open on dispose.</param>
    public static LzmaStream Create(
        byte[] properties,
        Stream inputStream,
        long inputSize = -1,
        long outputSize = -1,
        Stream? presetDictionary = null,
        bool leaveOpen = false
    )
    {
        return Create(properties, inputStream, inputSize, outputSize, presetDictionary, properties.Length < 5, leaveOpen);
    }

    public static LzmaStream Create(
        byte[] properties,
        Stream inputStream,
        long inputSize,
        long outputSize,
        Stream? presetDictionary,
        bool isLzma2,
        bool leaveOpen = false
    )
    {
        var lzma = new LzmaStream(properties, inputStream, inputSize, outputSize, isLzma2, leaveOpen);
        if (!isLzma2)
        {
            if (presetDictionary != null)
            {
                lzma._outWindow.Train(presetDictionary);
            }

            lzma._rangeDecoder.Init(inputStream);
            // Bound the fast buffered reader to the known compressed size (when available) so
            // it never reads past this chunk's data even on unbounded/shared streams.
            lzma._rangeDecoder.SetFastLimit(lzma._rangeDecoderLimit);
        }
        else
        {
            if (presetDictionary != null)
            {
                lzma._outWindow.Train(presetDictionary);
                lzma._needDictReset = false;
            }
        }

        return lzma;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (disposing)
        {
            if (!_leaveOpen)
            {
                _inputStream?.Dispose();
            }

            _outWindow.Dispose();
        }

        base.Dispose(disposing);
    }

    public override long Length => _position + _availableBytes;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_endReached)
        {
            return 0;
        }

        var total = 0;
        while (total < count)
        {
            if (_availableBytes == 0)
            {
                if (_isLzma2)
                {
                    DecodeChunkHeader();
                }
                else
                {
                    _endReached = true;
                }

                if (_endReached)
                {
                    break;
                }
            }

            var toProcess = count - total;
            if (toProcess > _availableBytes)
            {
                toProcess = (int)_availableBytes;
            }

            _outWindow.SetLimit(toProcess);
            if (_uncompressedChunk)
            {
                CompressedBytesRead += _outWindow.CopyStream(_inputStream, toProcess);
            }
            else if (_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
            {
                HandleEndMarker();
            }

            var read = _outWindow.Read(buffer, offset, toProcess);
            total += read;
            offset += read;
            _position += read;
            _availableBytes -= read;

            if (_availableBytes == 0 && !_uncompressedChunk)
            {
                if (_isLzma2 && _decoder!.HasEndMarker)
                {
                    throw new DataErrorException();
                }

                // Check range corruption scenario
                if (
                    !_rangeDecoder.IsFinished
                    || (_rangeDecoderLimit >= 0 && _rangeDecoder.Total != _rangeDecoderLimit)
                )
                {
                    // Stream might have End Of Stream marker
                    _outWindow.SetLimit(toProcess + 1);
                    if (!_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
                    {
                        _rangeDecoder.ReleaseStream();
                        throw new DataErrorException();
                    }
                }

                _rangeDecoder.ReleaseStream();

                CompressedBytesRead += _rangeDecoder.Total;
                if (_outWindow.HasPending)
                {
                    throw new DataErrorException();
                }
            }
        }

        if (_endReached)
        {
            if ((_inputSize >= 0 && CompressedBytesRead != _inputSize) || (_outputSize >= 0 && _position != _outputSize))
            {
                throw new DataErrorException();
            }
        }

        return total;
    }

    public override int ReadByte()
    {
        if (_endReached)
        {
            return -1;
        }

        if (_availableBytes == 0)
        {
            if (_isLzma2)
            {
                DecodeChunkHeader();
            }
            else
            {
                _endReached = true;
            }
        }

        if (_endReached)
        {
            if ((_inputSize >= 0 && CompressedBytesRead != _inputSize) || (_outputSize >= 0 && _position != _outputSize))
            {
                throw new DataErrorException();
            }

            return -1;
        }

        _outWindow.SetLimit(1);
        if (_uncompressedChunk)
        {
            CompressedBytesRead += _outWindow.CopyStream(_inputStream, 1);
        }
        else if (_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
        {
            HandleEndMarker();
        }

        var value = _outWindow.ReadByte();
        _position++;
        _availableBytes--;

        if (_availableBytes == 0 && !_uncompressedChunk)
        {
            if (_isLzma2 && _decoder!.HasEndMarker)
            {
                throw new DataErrorException();
            }

            // Check range corruption scenario
            if (
                !_rangeDecoder.IsFinished
                || (_rangeDecoderLimit >= 0 && _rangeDecoder.Total != _rangeDecoderLimit)
            )
            {
                // Stream might have End Of Stream marker
                _outWindow.SetLimit(2);
                if (!_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
                {
                    _rangeDecoder.ReleaseStream();
                    throw new DataErrorException();
                }
            }

            _rangeDecoder.ReleaseStream();

            CompressedBytesRead += _rangeDecoder.Total;
            if (_outWindow.HasPending)
            {
                throw new DataErrorException();
            }
        }

        return value;
    }

    private void DecodeChunkHeader()
    {
        var control = _inputStream!.ReadByte();
        CompressedBytesRead++;

        switch (control)
        {
            case 0x00 when _isLzma2 && _decoder is { HasEndMarker: true }:
                throw new DataErrorException();
            case 0x00:
                _endReached = true;
                return;
            case >= 0xE0 or 0x01:
                _needProps = true;
                _needDictReset = false;
                _outWindow.Reset();
                break;
            default:
                {
                    if (_needDictReset)
                    {
                        throw new DataErrorException();
                    }

                    break;
                }
        }

        switch (control)
        {
            case >= 0x80:
                {
                    _uncompressedChunk = false;

                    _availableBytes = (control & 0x1F) << 16;
                    _availableBytes += (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                    CompressedBytesRead += 2;

                    _rangeDecoderLimit = (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                    CompressedBytesRead += 2;

                    if (control >= 0xC0)
                    {
                        _needProps = false;
                        Properties[0] = (byte)_inputStream.ReadByte();
                        CompressedBytesRead++;

                        _decoder = new Decoder();
                        _decoder.SetDecoderProperties(Properties);
                    }
                    else if (_needProps)
                    {
                        throw new DataErrorException();
                    }
                    else if (control >= 0xA0)
                    {
                        _decoder = new Decoder();
                        _decoder.SetDecoderProperties(Properties);
                    }

                    _rangeDecoder.Init(_inputStream);
                    // LZMA2 chunks share one underlying stream with the raw chunk-header bytes read
                    // above/below, so the buffered fast-read path must never physically read past this
                    // chunk's compressed size, or it would desynchronize the stream position for the
                    // next chunk header.
                    _rangeDecoder.SetFastLimit(_rangeDecoderLimit);
                    break;
                }
            case > 0x02:
                throw new DataErrorException();
            default:
                _uncompressedChunk = true;
                _availableBytes = (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                CompressedBytesRead += 2;
                break;
        }
    }

    private void HandleEndMarker()
    {
        if (_isLzma2)
        {
            throw new DataErrorException();
        }

        if (_outputSize < 0)
        {
            _availableBytes = _outWindow.AvailableBytes;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public byte[] Properties { get; }

    internal long CompressedBytesRead { get; private set; }
}
