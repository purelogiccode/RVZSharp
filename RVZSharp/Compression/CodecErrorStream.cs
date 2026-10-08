namespace RVZSharp.Compression;

/// <summary>
/// Wraps a third-party decompression stream and maps any decoder failure to
/// <see cref="RvzFormatException"/>. The codec libraries (SharpZipLib, ZstdSharp, the
/// vendored LZMA decoder) can throw their own exception types — or plain index/argument
/// exceptions — on corrupt input; consumers of the library should only ever see
/// <see cref="RvzException"/> subclasses for malformed files.
/// </summary>
internal sealed class CodecErrorStream : Stream
{
    private readonly Stream _inner;
    private readonly string _codecName;

    /// <summary>Wraps <paramref name="inner"/> and names the codec for error messages.</summary>
    /// <param name="inner">The codec stream whose reads are guarded.</param>
    /// <param name="codecName">Codec name used in the <see cref="RvzFormatException"/> message.</param>
    public CodecErrorStream(Stream inner, string codecName)
    {
        _inner = inner;
        _codecName = codecName;
    }

    /// <summary>
    /// Creates a codec stream with <paramref name="factory"/>, mapping construction failures
    /// (some codecs parse their first block in the constructor) to <see cref="RvzFormatException"/>.
    /// </summary>
    /// <param name="factory">Creates the raw codec stream.</param>
    /// <param name="codecName">Codec name used in the <see cref="RvzFormatException"/> message.</param>
    /// <returns>The guarded codec stream.</returns>
    public static Stream Create(Func<Stream> factory, string codecName)
    {
        try
        {
            return new CodecErrorStream(factory(), codecName);
        }
        catch (RvzException)
        {
            throw;
        }
        catch (Exception e) when (e is not (IOException or OperationCanceledException
                                              or ObjectDisposedException))
        {
            throw new RvzFormatException(
                $"Failed to initialize a {codecName} stream: {e.Message}", e);
        }
    }

    /// <summary>True when the codec stream is readable.</summary>
    public override bool CanRead => _inner.CanRead;

    /// <summary>True when the codec stream is seekable (usually false).</summary>
    public override bool CanSeek => _inner.CanSeek;

    /// <summary>False; codec streams are read-only.</summary>
    public override bool CanWrite => false;

    /// <summary>Length of the codec stream (usually unsupported).</summary>
    public override long Length => _inner.Length;

    /// <summary>Position of the codec stream (usually unsupported).</summary>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>Reads from the codec, mapping decoder failures to <see cref="RvzFormatException"/>.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Guard(() => _inner.Read(buffer, offset, count));
    }

    /// <summary>Reads from the codec, mapping decoder failures to <see cref="RvzFormatException"/>.</summary>
    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _inner.Read(buffer);
        }
        catch (RvzException)
        {
            throw;
        }
        catch (Exception e) when (e is not (IOException or OperationCanceledException
                                              or ObjectDisposedException))
        {
            throw new RvzFormatException($"Failed to decompress a {_codecName} stream: {e.Message}", e);
        }
    }

    /// <summary>Reads one byte from the codec, mapping decoder failures to <see cref="RvzFormatException"/>.</summary>
    public override int ReadByte()
    {
        return Guard(_inner.ReadByte);
    }

    /// <summary>Seeks the codec stream when it supports seeking.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    /// <summary>No-op; codec streams have no write buffer.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported; codec streams are read-only.</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; codec streams are read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>Disposes the wrapped codec stream.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (RvzException)
        {
            throw;
        }
        catch (Exception e) when (e is not (IOException or OperationCanceledException
                                              or ObjectDisposedException))
        {
            throw new RvzFormatException($"Failed to decompress a {_codecName} stream: {e.Message}", e);
        }
    }
}
