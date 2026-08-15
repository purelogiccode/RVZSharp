#nullable disable

using System.Buffers;


namespace RVZSharp.Compression.Lzma.LZ;

/// <summary>
/// Circular output window buffer: decoded bytes are written into it, flushed to the output
/// stream when the buffer wraps, and match copies read back from the already-written area.
/// </summary>
internal class OutWindow : IDisposable
{
    private int _streamPos;
    private int _pendingLen;
    private int _pendingDist;
    private Stream _stream;

    /// <summary>Total number of bytes produced into the window so far.</summary>
    /// <returns>The total byte count.</returns>
    public long Total => FastTotal;

    // Fast-path accessors used by the local-variable LZMA decode loop (see
    // Decoder.Fast.cs). CodeFast snapshots pos/total/buffer into locals for the whole
    // decode call instead of going through PutByte/GetByte/CopyBlock (and re-reading the
    // fields of this object) for every single output byte, mirroring how the reference
    // 7-Zip C decoder caches dicPos/dic as locals for the duration of one decode call.
    /// <summary>The rented circular byte buffer used by the fast decode path.</summary>
    internal byte[] FastBuffer { get; private set; }

    /// <summary>Write position (wrapping) within the circular buffer.</summary>
    internal int FastPos { get; set; }

    /// <summary>Total bytes produced to the buffer and the underlying stream (monotonic).</summary>
    internal long FastTotal { get; set; }

    /// <summary>Size of the circular buffer (the dictionary/window size).</summary>
    internal int FastWindowSize { get; private set; }

    /// <summary>Upper bound of total output allowed for the current decode session.</summary>
    internal long FastLimit { get; private set; }

    /// <summary>Flushes buffered bytes to the underlying stream (window-wrap path).</summary>
    internal void FastFlush()
    {
        Flush();
    }

    /// <summary>Stores the residual of a match copy for completion by the next decode session.</summary>
    /// <param name="distance">The match distance of the pending copy.</param>
    /// <param name="len">Number of bytes still to copy.</param>
    internal void SetPendingFast(int distance, int len)
    {
        _pendingDist = distance;
        _pendingLen = len;
    }

    /// <summary>Allocates (or re-uses) the window buffer of the given size and resets all state.</summary>
    /// <param name="windowSize">Circular buffer size in bytes.</param>
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

    /// <summary>Flushes and returns the rented buffer to the array pool.</summary>
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

    /// <summary>Detaches the output stream and restarts with an empty window of the same size.</summary>
    public void Reset()
    {
        ReleaseStream();
        Create(FastWindowSize);
    }

    /// <summary>Binds the window to the stream that receives flushed bytes.</summary>
    /// <param name="stream">The destination stream.</param>
    public void Init(Stream stream)
    {
        ReleaseStream();
        _stream = stream;
    }

    /// <summary>Prefills the window with the trailing bytes of a stream (preset dictionary).</summary>
    /// <param name="stream">The stream whose final bytes become the dictionary.</param>
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

    /// <summary>Flushes pending bytes to the stream and detaches from it.</summary>
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

    /// <summary>Completes a leftover pending match copy from the previous decode session.</summary>
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

    /// <summary>Copies <c>len</c> bytes from the byte <c>distance</c> positions back in the window to the current position.</summary>
    /// <param name="distance">The match distance.</param>
    /// <param name="len">The number of bytes to copy.</param>
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

    /// <summary>Writes one byte to the window, flushing to the stream when the buffer wraps.</summary>
    /// <param name="b">The byte to append.</param>
    public void PutByte(byte b)
    {
        FastBuffer[FastPos++] = b;
        FastTotal++;
        if (FastPos >= FastWindowSize)
        {
            Flush();
        }
    }

    /// <summary>Reads the byte <c>distance</c> positions behind the current write position.</summary>
    /// <param name="distance">The match distance.</param>
    /// <returns>The referenced byte.</returns>
    public byte GetByte(int distance)
    {
        var pos = FastPos - distance - 1;
        if (pos < 0)
        {
            pos += FastWindowSize;
        }

        return FastBuffer[pos];
    }

    /// <summary>Copies up to <c>len</c> bytes directly from an uncompressed chunk stream into the window.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="len">Maximum number of bytes to copy.</param>
    /// <returns>The number of bytes actually copied.</returns>
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

    /// <summary>Sets the output limit of the current session to <c>size</c> more bytes.</summary>
    /// <param name="size">The number of bytes the session may still produce.</param>
    public void SetLimit(long size)
    {
        FastLimit = FastTotal + size;
    }

    /// <summary>Whether the window still accepts bytes (space left and output limit not reached).</summary>
    /// <returns>True when a byte can still be appended.</returns>
    public bool HasSpace => FastPos < FastWindowSize && FastTotal < FastLimit;

    /// <summary>Whether a partial pending copy remains from the last block copy.</summary>
    /// <returns>True when pending bytes are outstanding.</returns>
    public bool HasPending => _pendingLen > 0;

    /// <summary>Copies decoded, not-yet-consumed bytes out of the window into <c>buffer</c>.</summary>
    /// <param name="buffer">The destination array.</param>
    /// <param name="offset">Zero-based offset in <c>buffer</c> at which to begin storing bytes.</param>
    /// <param name="count">Maximum number of bytes to copy.</param>
    /// <returns>The number of bytes copied.</returns>
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

    /// <summary>Copies decoded bytes out of the window into a memory region.</summary>
    /// <param name="buffer">The destination memory.</param>
    /// <param name="offset">Zero-based offset in <c>buffer</c> at which to begin storing bytes.</param>
    /// <param name="count">Maximum number of bytes to copy.</param>
    /// <returns>The number of bytes copied.</returns>
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

    /// <summary>Returns the next decoded byte from the window, without consuming it from the window accounting.</summary>
    /// <returns>The byte value, or -1 when nothing is buffered.</returns>
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

    /// <summary>Number of decoded bytes buffered in the window but not yet read out.</summary>
    /// <returns>The count of available bytes.</returns>
    public int AvailableBytes => FastPos - _streamPos;
}
