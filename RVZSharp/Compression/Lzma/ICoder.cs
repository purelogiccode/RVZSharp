// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.
using System;
using System.IO;

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when an error in the input stream occurs during decoding.</summary>
internal sealed class DataErrorException : IOException
{
    public DataErrorException()
        : base("Data Error") { }

    public DataErrorException(string message)
        : base(message) { }
}

/// <summary>The exception that is thrown when the value of an argument is outside the allowable range.</summary>
internal sealed class InvalidParamException : IOException
{
    public InvalidParamException()
        : base("Invalid Parameter") { }

    public InvalidParamException(string message)
        : base(message) { }
}

public interface ICodeProgress
{
    /// <summary>Callback progress.</summary>
    /// <param name="inSize">input size. -1 if unknown.</param>
    /// <param name="outSize">output size. -1 if unknown.</param>
    void SetProgress(long inSize, long outSize);
}

internal interface ICoder
{
    /// <summary>
    /// Codes streams.
    /// </summary>
    /// <param name="inStream">input Stream.</param>
    /// <param name="outStream">output Stream.</param>
    /// <param name="inSize">input Size. -1 if unknown.</param>
    /// <param name="outSize">output Size. -1 if unknown.</param>
    /// <param name="progress">callback progress reference.</param>
    void Code(Stream inStream, Stream outStream, long inSize, long outSize, ICodeProgress progress);
}

internal interface ISetDecoderProperties
{
    void SetDecoderProperties(byte[] properties);
}
