// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when the value of an argument is outside the allowable range.</summary>
internal sealed class InvalidParamException : IOException
{
    /// <summary>Creates a new invalid-parameter exception with the default message.</summary>
    public InvalidParamException()
        : base("Invalid Parameter")
    {
    }

    /// <summary>Creates a new invalid-parameter exception with a custom message.</summary>
    /// <param name="message">The message describing the invalid parameter.</param>
    public InvalidParamException(string message)
        : base(message)
    {
    }
}