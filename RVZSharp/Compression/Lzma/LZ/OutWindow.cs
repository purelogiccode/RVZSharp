#nullable disable

using System.Buffers;


namespace RVZSharp.Compression.Lzma.LZ;

internal partial class OutWindow : IDisposable
{
    private byte[] _buffer;
    private int _windowSize;
    private int _streamPos;
    private int _pendingLen;
    private int _pendingDist;
    private Stream _stream;

    private long _limit;

    public long Total => FastTotal;

#if !LEGACY_DOTNET
    // Fast-path accessors used by the local-variable LZMA decode loop (see
    // LzmaDecoder.Fast.cs). CodeFast snapshots pos/total/buffer into locals for the whole
    // decode call instead of going through PutByte/GetByte/CopyBlock (and re-reading the
    // fields of this object) for every single output byte, mirroring how the reference
    // 7-Zip C decoder caches dicPos/dic as locals for the duration of one decode call.
    internal byte[] FastBuffer => _buffer;
    internal int FastPos { get; set; }

    internal long FastTotal { get; set; }

    internal int FastWindowSize => _windowSize;
    internal long FastLimit => _limit;

    internal void FastFlush()
    {
        Flush();
    }

    internal void SetPendingFast(int distance, int len)
    {
        _pendingDist = distance;
        _pendingLen = len;
    }
#endif

    public void Create(int windowSize)
    {
        if (windowSize <= 0)
        {
            throw new DataErrorException($"LZMA: invalid dictionary size {windowSize}");
        }
        if (_windowSize != windowSize)
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
            _buffer = ArrayPool<byte>.Shared.Rent(windowSize);
        }
        _buffer[windowSize - 1] = 0;
        _windowSize = windowSize;
        FastPos = 0;
        _streamPos = 0;
        _pendingLen = 0;
        FastTotal = 0;
        _limit = 0;
    }

    public void Dispose()
    {
        ReleaseStream();
        if (_buffer is null)
        {
            return;
        }
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
    }

    public void Reset()
    {
        ReleaseStream();
        Create(_windowSize);
    }

    public void Init(Stream stream)
    {
        ReleaseStream();
        _stream = stream;
    }

    public void Train(Stream stream)
    {
        var len = stream.Length;
        var size = (len < _windowSize) ? (int)len : _windowSize;
        stream.Position = len - size;
        FastTotal = 0;
        _limit = size;
        FastPos = _windowSize - size;
        CopyStream(stream, size);
        if (FastPos == _windowSize)
        {
            FastPos = 0;
        }
        _streamPos = FastPos;
    }

    public void ReleaseStream()
    {
        Flush();
        _stream = null;
    }

    private void Flush()
    {
        if (_stream is null)
        {
            return;
        }
        var size = FastPos - _streamPos;
        if (size == 0)
        {
            return;
        }
        _stream.Write(_buffer, _streamPos, size);
        if (FastPos >= _windowSize)
        {
            FastPos = 0;
        }
        _streamPos = FastPos;
    }

    public void CopyPending()
    {
        if (_pendingLen < 1)
        {
            return;
        }
        var rem = _pendingLen;
        var pos = (_pendingDist < FastPos ? FastPos : FastPos + _windowSize) - _pendingDist - 1;
        while (rem > 0 && HasSpace)
        {
            if (pos >= _windowSize)
            {
                pos = 0;
            }
            PutByte(_buffer[pos++]);
            rem--;
        }
        _pendingLen = rem;
    }

    public void CopyBlock(int distance, int len)
    {
        var rem = len;
        var pos = (distance < FastPos ? FastPos : FastPos + _windowSize) - distance - 1;
        var targetSize = HasSpace ? (int)Math.Min(rem, _limit - FastTotal) : 0;
        var sizeUntilWindowEnd = Math.Min(_windowSize - FastPos, _windowSize - pos);
        var sizeUntilOverlap = Math.Abs(pos - FastPos);
        var fastSize = Math.Min(Math.Min(sizeUntilWindowEnd, sizeUntilOverlap), targetSize);
        if (fastSize >= 2)
        {
            _buffer.AsSpan(pos, fastSize).CopyTo(_buffer.AsSpan(FastPos, fastSize));
            FastPos += fastSize;
            pos += fastSize;
            FastTotal += fastSize;
            if (FastPos >= _windowSize)
            {
                Flush();
            }
            rem -= fastSize;
        }
        while (rem > 0 && HasSpace)
        {
            if (pos >= _windowSize)
            {
                pos = 0;
            }
            PutByte(_buffer[pos++]);
            rem--;
        }
        _pendingLen = rem;
        _pendingDist = distance;
    }

    public void PutByte(byte b)
    {
        _buffer[FastPos++] = b;
        FastTotal++;
        if (FastPos >= _windowSize)
        {
            Flush();
        }
    }

    public byte GetByte(int distance)
    {
        var pos = FastPos - distance - 1;
        if (pos < 0)
        {
            pos += _windowSize;
        }
        return _buffer[pos];
    }

    public int CopyStream(Stream stream, int len)
    {
        var size = len;
        while (size > 0 && FastPos < _windowSize && FastTotal < _limit)
        {
            var curSize = _windowSize - FastPos;
            if (curSize > _limit - FastTotal)
            {
                curSize = (int)(_limit - FastTotal);
            }
            if (curSize > size)
            {
                curSize = size;
            }
            var numReadBytes = stream.Read(_buffer, FastPos, curSize);
            if (numReadBytes == 0)
            {
                throw new DataErrorException();
            }
            size -= numReadBytes;
            FastPos += numReadBytes;
            FastTotal += numReadBytes;
            if (FastPos >= _windowSize)
            {
                Flush();
            }
        }
        return len - size;
    }

    public void SetLimit(long size)
    {
        _limit = FastTotal + size;
    }

    public bool HasSpace => FastPos < _windowSize && FastTotal < _limit;

    public bool HasPending => _pendingLen > 0;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_streamPos >= FastPos)
        {
            return 0;
        }

        var size = FastPos - _streamPos;
        if (size > count)
        {
            size = count;
        }
        Buffer.BlockCopy(_buffer, _streamPos, buffer, offset, size);
        _streamPos += size;
        if (_streamPos >= _windowSize)
        {
            FastPos = 0;
            _streamPos = 0;
        }
        return size;
    }

    public int Read(Memory<byte> buffer, int offset, int count)
    {
        if (_streamPos >= FastPos)
        {
            return 0;
        }

        var size = FastPos - _streamPos;
        if (size > count)
        {
            size = count;
        }
        _buffer.AsMemory(_streamPos, size).CopyTo(buffer.Slice(offset, size));
        _streamPos += size;
        if (_streamPos >= _windowSize)
        {
            FastPos = 0;
            _streamPos = 0;
        }
        return size;
    }

    public int ReadByte()
    {
        if (_streamPos >= FastPos)
        {
            return -1;
        }

        int value = _buffer[_streamPos];

        _streamPos++;
        if (_streamPos >= _windowSize)
        {
            FastPos = 0;
            _streamPos = 0;
        }

        return value;
    }

    public int AvailableBytes => FastPos - _streamPos;
}
