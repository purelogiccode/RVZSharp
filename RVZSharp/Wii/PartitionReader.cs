using System.Security.Cryptography;
using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Wii;

/// <summary>
/// A decrypted view of one Wii partition (Dolphin: <c>VolumeWii::Read</c> with a partition):
/// offsets are partition-relative (0 = the start of the partition's data area) and sector data
/// is decrypted on demand. Each 0x8000-byte sector stores a 0x400-byte hash area followed by
/// 0x7C00 bytes of data; the data is AES-128-CBC encrypted with the partition key and the
/// ciphertext at 0x3D0 as IV (Dolphin: <c>DecryptBlockData</c>). Discs without hash trees
/// store the partition data as-is and are read through directly.
/// </summary>
public sealed class PartitionReader : IBlobReader
{
    private const int BlockDataSize = WiiHashCalculator.SectorDataSize; // 0x7C00
    private const int BlockTotalSize = PartitionRegionBuilder.SectorSize; // 0x8000
    private const int BlockHeaderSize = WiiHashCalculator.HashBlockSize; // 0x400

    private readonly IBlobReader _disc;
    private readonly long _dataOffset;
    private readonly bool _hasHashes;
    private readonly bool _hasEncryption;
    private readonly Aes? _aes;
    private readonly byte[] _lastBlockData = new byte[BlockDataSize];
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private long _lastBlockIndex = -1;

    /// <summary>Wraps <paramref name="disc"/> with a decrypted view of <paramref name="partition"/>.</summary>
    /// <param name="disc">The disc image (the partition is read from its original location).</param>
    /// <param name="partition">The partition to expose, as returned by <see cref="WiiVolume.GetPartitions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="disc"/> is null.</exception>
    public PartitionReader(IBlobReader disc, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disc);
        _disc = disc;
        Partition = partition;
        _dataOffset = (long)(partition.Offset + partition.DataOffset);
        _hasHashes = WiiVolume.HasWiiHashes(disc);
        _hasEncryption = WiiVolume.HasWiiEncryption(disc);

        var available = Math.Max(0, disc.Length - _dataOffset);
        Length = (long)Math.Min(partition.DataSize, (ulong)available);

        if (_hasEncryption)
        {
            _aes = Aes.Create();
            _aes.Key = partition.Key;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.None;
        }
    }

    /// <summary>The partition this reader decrypts.</summary>
    public Partition Partition { get; }

    /// <summary>The container type of the underlying disc.</summary>
    public BlobType Type => _disc.Type;

    /// <summary>Size of the decrypted partition data in bytes.</summary>
    public long Length { get; }

    /// <summary>The encrypted sector size (0x8000).</summary>
    public int BlockSize => BlockTotalSize;

    /// <summary>
    /// Reads <paramref name="buffer.Length"/> bytes at the partition-relative
    /// <paramref name="position"/>, decrypting sectors on demand.
    /// </summary>
    /// <param name="position">Offset into the decrypted partition data.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of bytes read (fewer at the end of the partition).</returns>
    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        // Discs without hash trees store the partition data as-is (Dolphin: VolumeWii::Read).
        if (!_hasHashes)
        {
            return _disc.ReadAt(_dataOffset + position, buffer);
        }

        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var blockIndex = position / BlockDataSize;
            var offsetInBlock = (int)(position % BlockDataSize);
            var take = (int)Math.Min(
                Math.Min(buffer.Length, BlockDataSize - offsetInBlock), Length - position);

            lock (_lock)
            {
                if (_lastBlockIndex != blockIndex)
                {
                    ReadAndDecryptBlock(blockIndex);
                    _lastBlockIndex = blockIndex;
                }

                _lastBlockData.AsSpan(offsetInBlock, take).CopyTo(buffer);
            }

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    private void ReadAndDecryptBlock(long blockIndex)
    {
        var encrypted = new byte[BlockTotalSize];
        var read = _disc.ReadAt(_dataOffset + blockIndex * BlockTotalSize, encrypted);
        if (read != BlockTotalSize)
        {
            throw new RvzFormatException(
                $"Partition read at 0x{_dataOffset + blockIndex * BlockTotalSize:X} returned "
                + $"{read} of {BlockTotalSize} bytes.");
        }

        if (_hasEncryption)
        {
            var iv = encrypted.AsSpan(PartitionRegionBuilder.IvOffset, 16).ToArray();
            using var decryptor = _aes!.CreateDecryptor(_aes.Key, iv);
            decryptor.TransformBlock(encrypted, BlockHeaderSize, BlockDataSize, _lastBlockData, 0);
        }
        else
        {
            encrypted.AsSpan(BlockHeaderSize, BlockDataSize).CopyTo(_lastBlockData);
        }
    }

    /// <summary>
    /// Releases the AES context; the wrapped disc is owned by the caller and is never disposed.
    /// </summary>
    public void Dispose()
    {
        _aes?.Dispose();
    }
}
