// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

using System.Diagnostics.CodeAnalysis;

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when an error in the input stream occurs during decoding.</summary>
[SuppressMessage("Roslynator", "RCS1194", Justification = "Only the default and message constructors are used.")]
internal sealed class DataErrorException : IOException
{
    /// <summary>Creates a new data-error exception with the default message.</summary>
    public DataErrorException()
        : base("Data Error")
    {
    }

    /// <summary>Creates a new data-error exception with a custom message.</summary>
    /// <param name="message">The message describing the decoding error.</param>
    public DataErrorException(string message)
        : base(message)
    {
    }
}
