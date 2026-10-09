// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

using System.Diagnostics.CodeAnalysis;

namespace RVZSharp.Compression.Lzma;

/// <summary>The exception that is thrown when the value of an argument is outside the allowable range.</summary>
[SuppressMessage("Roslynator", "RCS1194", Justification = "Only the default constructor is used.")]
internal sealed class InvalidParamException : IOException
{
    /// <summary>Creates a new invalid-parameter exception with the default message.</summary>
    public InvalidParamException()
        : base("Invalid Parameter")
    {
    }
}
