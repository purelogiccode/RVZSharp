// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

namespace RVZSharp.Interfaces;

/// <summary>The LZMA coder contract (LZMA SDK convention).</summary>
internal interface ICoder
{
    /// <summary>Codes the input stream into the output stream with the given sizes.</summary>
    /// <param name="inStream">The compressed input stream.</param>
    /// <param name="outStream">The decoded output stream.</param>
    /// <param name="inSize">The compressed size, or -1 if unknown.</param>
    /// <param name="outSize">The decoded size, or -1 if unknown.</param>
    /// <param name="progress">Optional progress callback; can be null.</param>
    void Code(Stream inStream, Stream outStream, long inSize, long outSize, ICodeProgress progress);
}