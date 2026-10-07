using System.Security.Cryptography;
using RVZSharp.Interfaces;
using RVZSharp.IO;
using RVZSharp.Models;

namespace RVZSharp;

/// <summary>
/// Computes the CRC-32, MD5 and SHA-1 hashes of a decoded disc image in a single streaming
/// pass (Dolphin: VolumeVerifier; DolphinTool: verify). Use it to verify that a container
/// decodes to the expected image without materializing the whole ISO in memory. The
/// container's own integrity (SHA-1s, structure) is validated by
/// <see cref="RvzReader.Open(Stream, bool)"/> / <see cref="RvzReader.OpenWia(Stream, bool)"/>
/// at open time, so no separate container check is needed.
/// </summary>
public static class DiscHasher
{
    /// <summary>
    /// Hashes the decoded image served by <paramref name="image"/> in 1 MiB blocks.
    /// </summary>
    /// <param name="image">The disc image to hash (any format).</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes hashed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The CRC-32, MD5 and SHA-1 of the decoded image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="RvzFormatException">Decoding stopped before the end of the image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static DiscHashes Compute(IBlobReader image, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        var crc = 0xFFFFFFFFu;
        var position = 0L;
        while (position < image.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(buffer.Length, image.Length - position);
            var read = image.ReadAt(position, buffer.AsSpan(0, take));
            if (read <= 0)
            {
                throw new RvzFormatException($"Hashing stopped at offset 0x{position:X}.");
            }

            var span = buffer.AsSpan(0, read);
            crc = Crc32.Update(crc, span);
            md5.AppendData(span);
            sha1.AppendData(span);
            position += read;
            progress?.Report((double)position / image.Length);
        }

        return new DiscHashes
        {
            Crc32 = crc ^ 0xFFFFFFFF,
            Md5 = md5.GetHashAndReset(),
            Sha1 = sha1.GetHashAndReset()
        };
    }
}
