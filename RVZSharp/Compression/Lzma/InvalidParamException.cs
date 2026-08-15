// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when the value of an argument is outside the allowable range.</summary>
internal sealed class InvalidParamException : IOException
{
    public InvalidParamException()
        : base("Invalid Parameter")
    {
    }

    public InvalidParamException(string message)
        : base(message)
    {
    }
}