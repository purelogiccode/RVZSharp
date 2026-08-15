// Adapted from SharpCompress (https://github.com/adamhathcock/sharpcompress), MIT license.
// See THIRD-PARTY-NOTICES.md in the repository root for the full license text.
namespace RVZSharp.Interfaces;

public interface ICodeProgress
{
    /// <summary>Callback progress.</summary>
    /// <param name="inSize">input size. -1 if unknown.</param>
    /// <param name="outSize">output size. -1 if unknown.</param>
    void SetProgress(long inSize, long outSize);
}