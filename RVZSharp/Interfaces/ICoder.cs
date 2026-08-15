// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.

namespace RVZSharp.Interfaces;

internal interface ICoder
{
    void Code(Stream inStream, Stream outStream, long inSize, long outSize, ICodeProgress progress);
}