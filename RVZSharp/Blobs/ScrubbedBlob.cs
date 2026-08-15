using RVZSharp.Wii;
using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Blobs;

/// <summary>
/// Wraps a disc image and zeroes the data of every non-game Wii partition (update,
/// channel, etc.) — the safe subset of Dolphin's DiscScrubber (DiscScrubber.cpp) that
/// does not need a filesystem (FST) parser. The game partition (type 0) and all raw
/// areas are served unchanged; the output image keeps its size.
/// </summary>
public sealed class ScrubbedBlob : IBlobReader
{
    private readonly IBlobReader _inner;
    private readonly List<(long Start, long End)> _scrubbed = [];

    private ScrubbedBlob(IBlobReader inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Creates a scrubbing wrapper, or returns null when the disc cannot be scrubbed
    /// (not a Wii disc, or no game partition found) — mirroring Dolphin's
    /// ScrubbedBlob::Create failure.
    /// </summary>
    public static ScrubbedBlob? Create(IBlobReader input)
    {
        if (!WiiVolume.IsWiiDisc(input))
        {
            return null;
        }

        var partitions = WiiVolume.GetPartitions(input);
        var game = partitions.FirstOrDefault(p => p.Type == 0);
        if (game.Offset == 0)
        {
            return null; // no game partition (offset 0 is the disc header)
        }

        var blob = new ScrubbedBlob(input);
        foreach (var partition in partitions)
        {
            if (partition.Type == 0)
            {
                continue;
            }

            var start = (long)(partition.Offset + partition.DataOffset);
            var end = Math.Min(input.Length, start + (long)partition.DataSize);
            if (end > start)
            {
                blob._scrubbed.Add((start, end));
            }
        }

        blob._scrubbed.Sort((a, b) => a.Start.CompareTo(b.Start));
        return blob;
    }

    public BlobType Type => _inner.Type;
    public long Length => _inner.Length;
    public int BlockSize => _inner.BlockSize;

    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var total = 0;
        var pos = position;
        while (!buffer.IsEmpty && pos < Length)
        {
            var range = _scrubbed.FirstOrDefault(r => r.Start <= pos && pos < r.End);
            int take;
            if (range != default)
            {
                // Inside a scrubbed partition's data: serve zeroes.
                take = (int)Math.Min(buffer.Length, range.End - pos);
                buffer[..take].Clear();
            }
            else
            {
                // Copy from the inner blob up to the next scrubbed range (or the end).
                var next = _scrubbed.FirstOrDefault(r => r.Start > pos);
                var limit = next != default ? next.Start : Length;
                take = (int)Math.Min(buffer.Length, limit - pos);
                take = _inner.ReadAt(pos, buffer[..take]);
                if (take <= 0)
                {
                    break;
                }
            }

            pos += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
