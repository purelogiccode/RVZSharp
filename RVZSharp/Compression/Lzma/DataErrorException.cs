// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when an error in the input stream occurs during decoding.</summary>
internal sealed class DataErrorException : IOException
{
    public DataErrorException()
        : base("Data Error")
    {
    }

    public DataErrorException(string message)
        : base(message)
    {
    }
}