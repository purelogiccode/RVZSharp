#nullable disable

using System.Buffers;


namespace RVZSharp.Compression.Lzma.LZ;

internal class OutWindow : IDisposable
{
    private int _streamPos;
    private int _pendingLen;
    private int _pendingDist;
    private Stream _stream;

    public long Total => FastTotal;

#if !LEGACY_DOTNET
    // Fast-path accessors used by the local-variable LZMA decode loop (see
    // LzmaDecoder.Fast.cs). CodeFast snapshots pos/total/buffer into locals for the whole
    // decode call instead of going through PutByte/GetByte/CopyBlock (and re-reading the
    // fields of this object) for every single output byte, mirroring how the reference
    // 7-Zip C decoder caches dicPos/dic as locals for the duration of one decode call.
    internal byte[] FastBuffer { get; private set; }

    internal int FastPos { get; set; }

    internal long FastTotal { get; set; }

    internal int FastWindowSize { get; private set; }

    internal long FastLimit { get; private set; }

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

        if (FastWindowSize != windowSize)
        {
            if (FastBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(FastBuffer);
            }

            FastBuffer = ArrayPool<byte>.Shared.Rent(windowSize);
        }

        FastBuffer[windowSize - 1] = 0;
        FastWindowSize = windowSize;
        FastPos = 0;
        _streamPos = 0;
        _pendingLen = 0;
        FastTotal = 0;
        FastLimit = 0;
    }

    public void Dispose()
    {
        ReleaseStream();
        if (FastBuffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(FastBuffer);
        FastBuffer = null;
    }

    public void Reset()
    {
        ReleaseStream();
        Create(FastWindowSize);
    }

    public void Init(Stream stream)
    {
        ReleaseStream();
        _stream = stream;
    }

    public void Train(Stream stream)
    {
        var len = stream.Length;
        var size = (len < FastWindowSize) ? (int)len : FastWindowSize;
        stream.Position = len - size;
        FastTotal = 0;
        FastLimit = size;
        FastPos = FastWindowSize - size;
        CopyStream(stream, size);
        if (FastPos == FastWindowSize)
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

        _stream.Write(FastBuffer, _streamPos, size);
        if (FastPos >= FastWindowSize)
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
        var pos = (_pendingDist < FastPos ? FastPos : FastPos + FastWindowSize) - _pendingDist - 1;
        while (rem > 0 && HasSpace)
        {
            if (pos >= FastWindowSize)
            {
                pos = 0;
            }

            PutByte(FastBuffer[pos++]);
            rem--;
        }

        _pendingLen = rem;
    }

    public void CopyBlock(int distance, int len)
    {
        var rem = len;
        var pos = (distance < FastPos ? FastPos : FastPos + FastWindowSize) - distance - 1;
        var targetSize = HasSpace ? (int)Math.Min(rem, FastLimit - FastTotal) : 0;
        var sizeUntilWindowEnd = Math.Min(FastWindowSize - FastPos, FastWindowSize - pos);
        var sizeUntilOverlap = Math.Abs(pos - FastPos);
        var fastSize = Math.Min(Math.Min(sizeUntilWindowEnd, sizeUntilOverlap), targetSize);
        if (fastSize >= 2)
        {
            FastBuffer.AsSpan(pos, fastSize).CopyTo(FastBuffer.AsSpan(FastPos, fastSize));
            FastPos += fastSize;
            pos += fastSize;
            FastTotal += fastSize;
            if (FastPos >= FastWindowSize)
            {
                Flush();
            }

            rem -= fastSize;
        }

        while (rem > 0 && HasSpace)
        {
            if (pos >= FastWindowSize)
            {
                pos = 0;
            }

            PutByte(FastBuffer[pos++]);
            rem--;
        }

        _pendingLen = rem;
        _pendingDist = distance;
    }

    public void PutByte(byte b)
    {
        FastBuffer[FastPos++] = b;
        FastTotal++;
        if (FastPos >= FastWindowSize)
        {
            Flush();
        }
    }

    public byte GetByte(int distance)
    {
        var pos = FastPos - distance - 1;
        if (pos < 0)
        {
            pos += FastWindowSize;
        }

        return FastBuffer[pos];
    }

    public int CopyStream(Stream stream, int len)
    {
        var size = len;
        while (size > 0 && FastPos < FastWindowSize && FastTotal < FastLimit)
        {
            var curSize = FastWindowSize - FastPos;
            if (curSize > FastLimit - FastTotal)
            {
                curSize = (int)(FastLimit - FastTotal);
            }

            if (curSize > size)
            {
                curSize = size;
            }

            var numReadBytes = stream.Read(FastBuffer, FastPos, curSize);
            if (numReadBytes == 0)
            {
                throw new DataErrorException();
            }

            size -= numReadBytes;
            FastPos += numReadBytes;
            FastTotal += numReadBytes;
            if (FastPos >= FastWindowSize)
            {
                Flush();
            }
        }

        return len - size;
    }

    public void SetLimit(long size)
    {
        FastLimit = FastTotal + size;
    }

    public bool HasSpace => FastPos < FastWindowSize && FastTotal < FastLimit;

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

        Buffer.BlockCopy(FastBuffer, _streamPos, buffer, offset, size);
        _streamPos += size;
        if (_streamPos >= FastWindowSize)
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

        FastBuffer.AsMemory(_streamPos, size).CopyTo(buffer.Slice(offset, size));
        _streamPos += size;
        if (_streamPos >= FastWindowSize)
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

        int value = FastBuffer[_streamPos];

        _streamPos++;
        if (_streamPos >= FastWindowSize)
        {
            FastPos = 0;
            _streamPos = 0;
        }

        return value;
    }

    public int AvailableBytes => FastPos - _streamPos;
}
