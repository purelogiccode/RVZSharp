using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp;

/// <summary>
/// Encodes any decoded disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>)
/// into the RVZ format, mirroring Dolphin's ConvertToWIAOrRVZ: Wii partition data is stored
/// decrypted with hash exceptions, raw data is stored as-is, and (optionally) PRNG junk is
/// packed with a recovered seed.
/// </summary>
public static class RvzWriter
{
    /// <summary>
    /// Writes <paramref name="input"/> as an RVZ file to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method).</param>
    /// <param name="options">Writer options; <see cref="RvzWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the input bytes processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between group reads.</param>
    /// <exception cref="ArgumentException">Invalid chunk size or input too small.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).
    /// </exception>
    /// <exception cref="RvzUnsupportedException">PURGE compression requested (WIA-only method).</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output, RvzWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        WiaRvzWriter.Write(input, output, WiaRvzFormat.Rvz, options, progress, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of <see cref="Write"/>. Encoding is synchronous and CPU-bound, so
    /// the work runs on the thread pool; the returned task completes when the file has been
    /// written.
    /// </summary>
    /// <param name="input">A GameCube or Wii disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method).</param>
    /// <param name="options">Writer options; <see cref="RvzWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the input bytes processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between group reads.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    /// <exception cref="ArgumentException">Invalid chunk size or input too small.</exception>
    /// <exception cref="RvzFormatException">
    /// The input is not a GameCube or Wii disc image (no disc header magic at 0x18/0x1C).
    /// </exception>
    /// <exception cref="RvzUnsupportedException">PURGE compression requested (WIA-only method).</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static Task WriteAsync(IBlobReader input, Stream output, RvzWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Write(input, output, options, progress, cancellationToken), cancellationToken);
    }
}
