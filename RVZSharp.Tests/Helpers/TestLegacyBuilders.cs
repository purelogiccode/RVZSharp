using System.IO.Compression;
using System.Security.Cryptography;
using RVZSharp.Blobs;

namespace RVZSharp.Tests.Helpers;

/// <summary>
/// Builds synthetic legacy-format files (GCZ, CISO, WBFS, TGC, NFS) for round-trip tests.
/// </summary>
public static class TestLegacyBuilders
{
    // --- GCZ --------------------------------------------------------------------------------

    /// <summary>
    /// Builds a GCZ file from an ISO. Blocks that compress to less than their raw size are
    /// zlib-compressed; the rest are stored raw (top bit of the offset table set), matching
    /// Dolphin's ConvertToGCZ. The last partial block is zero-padded to the block size.
    /// </summary>
    public static byte[] BuildGcz(byte[] iso, int blockSize = 0x4000, bool compress = true)
    {
        var numBlocks = (iso.Length + blockSize - 1) / blockSize;
        var stored = new List<byte[]>();
        var offsets = new long[numBlocks];
        var flags = new bool[numBlocks];
        var hashes = new uint[numBlocks];

        for (var i = 0; i < numBlocks; i++)
        {
            var block = new byte[blockSize];
            Array.Copy(iso, i * blockSize, block, 0,
                Math.Min(blockSize, iso.Length - i * blockSize));

            byte[] bytes = block;
            var isRaw = true;
            if (compress)
            {
                using var ms = new MemoryStream();
                using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(block);
                }

                if (ms.Length < blockSize)
                {
                    bytes = ms.ToArray();
                    isRaw = false;
                }
            }

            offsets[i] = stored.Sum(b => (long)b.Length);
            flags[i] = isRaw;
            hashes[i] = Adler32ForTest(bytes);
            stored.Add(bytes);
        }

        var dataSize = offsets[^1] + stored[^1].Length;
        using var output = new MemoryStream();
        WriteLe32(output, 0xB10BC001);
        WriteLe32(output, 0); // sub_type
        WriteLe64(output, (ulong)dataSize);
        WriteLe64(output, (ulong)iso.Length);
        WriteLe32(output, (uint)blockSize);
        WriteLe32(output, (uint)numBlocks);

        for (var i = 0; i < numBlocks; i++)
        {
            var pointer = (ulong)offsets[i] | (flags[i] ? 1UL << 63 : 0);
            WriteLe64(output, pointer);
        }

        foreach (var hash in hashes)
        {
            WriteLe32(output, hash);
        }

        foreach (var bytes in stored)
        {
            output.Write(bytes);
        }

        return output.ToArray();
    }

    public static uint Adler32ForTest(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    // --- CISO -------------------------------------------------------------------------------

    /// <summary>
    /// Builds a CISO file. <paramref name="presentBlocks"/> lists the block indices whose
    /// content is stored; all other blocks decode to zeroes. The decoded image is
    /// 0x7FF8 × blockSize bytes, so tests should only compare the interesting prefix.
    /// </summary>
    public static byte[] BuildCiso(byte[] iso, int blockSize, IEnumerable<int> presentBlocks)
    {
        var present = presentBlocks.ToHashSet();
        var map = new byte[CisoBlob.MapSize];
        foreach (var block in present)
        {
            if (block >= CisoBlob.MapSize)
            {
                throw new ArgumentOutOfRangeException(nameof(presentBlocks));
            }

            map[block] = 1;
        }

        using var output = new MemoryStream();
        var header = new byte[CisoBlob.HeaderSize];
        "CISO"u8.CopyTo(header);
        header[4] = (byte)blockSize;
        header[5] = (byte)(blockSize >> 8);
        header[6] = (byte)(blockSize >> 16);
        header[7] = (byte)(blockSize >> 24);
        map.CopyTo(header, 8);
        output.Write(header);

        for (var block = 0; block < CisoBlob.MapSize; block++)
        {
            if (!present.Contains(block))
            {
                continue;
            }

            var start = block * blockSize;
            var take = Math.Min(blockSize, iso.Length - start);
            output.Write(iso, start, take);
            output.Write(new byte[blockSize - take]);
        }

        return output.ToArray();
    }

    // --- WBFS -------------------------------------------------------------------------------

    /// <summary>
    /// Builds a standalone .wbfs file from an ISO. Disc clusters that cover the ISO are
    /// stored sequentially after the header/tables; clusters past the end of the ISO are
    /// mapped to a shared zero-filled cluster (the way scrubbers store empty regions). The
    /// decoded image is always the fixed Wii double-layer size, so tests compare the ISO
    /// prefix + zero tail.
    /// </summary>
    public static byte[] BuildWbfs(byte[] iso, int clusterSize = 0x10000, int hdSectorSize = 512)
    {
        var wiiDataSize = WbfsBlob.WiiDataSize;
        var blocksPerDisc = (int)((wiiDataSize + clusterSize - 1) / clusterSize);

        // The volume clusters are numbered from 0; cluster 0 holds the header + tables, so
        // disc data starts at the first cluster-aligned offset after the tables.
        var tableOffset = hdSectorSize + 256;
        var tableSize = blocksPerDisc * 2L;
        var dataClusterBase = (int)((tableOffset + tableSize + clusterSize - 1) / clusterSize);
        var dataStart = (long)dataClusterBase * clusterSize;

        var clusters = new List<byte[]>();
        var wlba = new ushort[blocksPerDisc];
        for (var i = 0; i < blocksPerDisc; i++)
        {
            var start = (long)i * clusterSize;
            if (start >= iso.Length)
            {
                continue; // mapped to the shared zero cluster below
            }

            var cluster = new byte[clusterSize];
            Array.Copy(iso, start, cluster, 0,
                (int)Math.Min(clusterSize, iso.Length - start));
            clusters.Add(cluster);
        }

        var zeroClusterIndex = dataClusterBase + clusters.Count;
        var zeroCluster = new byte[clusterSize];
        clusters.Add(zeroCluster);
        for (var i = 0; i < blocksPerDisc; i++)
        {
            var start = (long)i * clusterSize;
            if (start < iso.Length)
            {
                continue; // already mapped
            }

            wlba[i] = (ushort)zeroClusterIndex;
        }

        // Map the content clusters to their real volume indices.
        var clusterIndex = dataClusterBase;
        for (var i = 0; i < blocksPerDisc && clusterIndex < zeroClusterIndex; i++)
        {
            var start = (long)i * clusterSize;
            if (start < iso.Length)
            {
                wlba[i] = (ushort)clusterIndex++;
            }
        }

        var totalLength = dataStart + (long)clusters.Count * clusterSize;
        var fileLength = (totalLength + hdSectorSize - 1) / hdSectorSize * hdSectorSize;

        using var output = new MemoryStream();
        var header = new byte[hdSectorSize];
        "WBFS"u8.CopyTo(header);
        var hdSectorCount = (uint)(fileLength / hdSectorSize);
        header[4] = (byte)(hdSectorCount >> 24);
        header[5] = (byte)(hdSectorCount >> 16);
        header[6] = (byte)(hdSectorCount >> 8);
        header[7] = (byte)hdSectorCount;
        header[8] = (byte)Math.Log2(hdSectorSize);
        header[9] = (byte)Math.Log2(clusterSize);
        header[12] = 1; // disc_table[0] (after the 2-byte padding at 10-11): a disc is present
        output.Write(header);

        output.Position = tableOffset;
        foreach (var entry in wlba)
        {
            output.WriteByte((byte)(entry >> 8));
            output.WriteByte((byte)entry);
        }

        output.Position = dataStart;
        foreach (var cluster in clusters)
        {
            output.Write(cluster);
        }

        var result = output.ToArray();
        Array.Resize(ref result, (int)fileLength);
        return result;
    }

    // --- TGC --------------------------------------------------------------------------------

    /// <summary>
    /// Builds a TGC file from parameters that exercise the DOL/FST offset patches and the FST
    /// relocation (shift = file_area_real - file_area_virtual - tgc_header_size, non-zero).
    /// Returns the TGC bytes and the ISO the Dolphin reader would produce from them.
    /// </summary>
    public static (byte[] Tgc, byte[] Iso) BuildTgc(
        uint tgcHeaderSize = 0x100,
        uint fstReal = 0x300,
        uint dolReal = 0x400,
        uint fileAreaReal = 0x1000,
        uint fileAreaVirtual = 0x2000,
        int fstSize = 0x30,
        int dolSize = 0x800,
        int isoSize = 0x4000)
    {
        var shift = unchecked(fileAreaReal - fileAreaVirtual - tgcHeaderSize);

        // FST: entry 0 = root directory (count of 3), entries 1..3 = files whose offsets are
        // relative to the file area. Dolphin relocates file entries (type byte 0) by +shift.
        var fst = new byte[fstSize];
        fst[0] = 1; // root: directory
        WriteBe32(fst, 8, 4); // entry count (root + 3 files)
        var fileOffsets = new[] { 0u, 0x100u, 0x200u };
        for (var i = 0; i < 3; i++)
        {
            var pos = (i + 1) * 12;
            fst[pos] = 0; // file
            fst[pos + 1] = (byte)('A' + i); // name
            WriteBe32(fst, pos + 4, fileOffsets[i]); // stored (file-area-relative)
            WriteBe32(fst, pos + 8, 0x100);
        }

        // Relocated FST as it ends up in the decoded ISO.
        var patchedFst = (byte[])fst.Clone();
        for (var i = 0; i < 3; i++)
        {
            var pos = (i + 1) * 12;
            WriteBe32(patchedFst, pos + 4, unchecked(fileOffsets[i] + shift));
        }

        var dol = new byte[dolSize];
        new Random(7).NextBytes(dol);

        var files = new byte[3][];
        for (var i = 0; i < 3; i++)
        {
            files[i] = new byte[0x100];
            new Random(10 + i).NextBytes(files[i]);
        }

        // The TGC file: header, then FST, DOL and file data at their real offsets, with the
        // rest of the file being the ISO data shifted by tgc_header_size.
        var tgcLength = (int)tgcHeaderSize + isoSize;
        var tgc = new byte[tgcLength];
        // The magic is the one little-endian field in the TGC header (Dolphin reads a native
        // u32 and compares it to TGC_MAGIC = 0xA2380FAE, TGCBlob.cpp:50).
        tgc[0] = 0xAE;
        tgc[1] = 0x0F;
        tgc[2] = 0x38;
        tgc[3] = 0xA2;
        WriteBe32(tgc, 8, tgcHeaderSize);
        WriteBe32(tgc, 12, 0x80); // disc_header_area_size
        WriteBe32(tgc, 16, fstReal);
        WriteBe32(tgc, 20, (uint)fstSize);
        WriteBe32(tgc, 24, (uint)fstSize);
        WriteBe32(tgc, 28, dolReal);
        WriteBe32(tgc, 32, (uint)dolSize);
        WriteBe32(tgc, 36, fileAreaReal);
        WriteBe32(tgc, 52, fileAreaVirtual);

        // Copy the DOL first so an overlapping FST (dol_real inside the FST area) wins.
        dol.CopyTo(tgc, (int)dolReal);
        fst.CopyTo(tgc, (int)fstReal);
        var filePos = (long)fileAreaReal;
        foreach (var file in files)
        {
            file.CopyTo(tgc, filePos);
            filePos += file.Length;
        }

        // The ISO the reader produces: TGC[tgc_header_size..] with the DOL/FST offsets and the
        // FST replaced (only where the patches land inside the image).
        var iso = new byte[isoSize];
        Array.Copy(tgc, (int)tgcHeaderSize, iso, 0, isoSize);
        WriteBe32(iso, 0x0420, unchecked(dolReal - tgcHeaderSize));
        WriteBe32(iso, 0x0424, unchecked(fstReal - tgcHeaderSize));
        var replacementFstOffset = unchecked((int)(fstReal - tgcHeaderSize));
        if (replacementFstOffset >= 0 && replacementFstOffset + fstSize <= isoSize)
        {
            Array.Copy(patchedFst, 0, iso, replacementFstOffset, fstSize);
        }

        return (tgc, iso);
    }

    // --- NFS --------------------------------------------------------------------------------

    /// <summary>
    /// Builds a single-file NFS (hif_000000.nfs content) from a decoded ISO. Blocks outside
    /// <paramref name="ranges"/> decode to zeroes; the file stores only the present blocks,
    /// AES-128-CBC encrypted per block index with the given key.
    /// </summary>
    public static (byte[] Nfs, byte[] Iso) BuildNfs(byte[] key, int blockCount,
        (uint Start, uint Num)[] ranges, int seed = 3)
    {
        var decoded = new byte[blockCount * 0x8000];
        var rng = new Random(seed);
        rng.NextBytes(decoded);
        decoded[0x61] = 1; // the reader forces this byte; keep the ISO consistent

        // Blocks outside the ranges are not stored and decode to zeroes.
        var present = new bool[blockCount];
        foreach (var (start, num) in ranges)
        {
            for (var block = start; block < start + num; block++)
            {
                present[block] = true;
            }
        }

        for (var block = 0; block < blockCount; block++)
        {
            if (!present[block])
            {
                Array.Clear(decoded, block * 0x8000, 0x8000);
            }
        }

        // Re-randomize the present blocks so the whole image isn't deterministic.
        foreach (var (start, num) in ranges)
        {
            for (var block = start; block < start + num; block++)
            {
                rng.NextBytes(decoded.AsSpan((int)block * 0x8000, 0x8000));
            }
        }

        decoded[0x61] = 1; // re-apply after the fill

        // Aes-128-CBC, IV = 8 zero bytes + big-endian block index (mirrors the reader).
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var output = new MemoryStream();
        var header = new byte[0x200];
        "EGGS"u8.CopyTo(header);
        WriteBe32(header, 0x04, 1); // version
        WriteBe32(header, 0x10, (uint)ranges.Length);
        for (var i = 0; i < ranges.Length; i++)
        {
            WriteBe32(header, 0x14 + i * 8, ranges[i].Start);
            WriteBe32(header, 0x18 + i * 8, ranges[i].Num);
        }

        "SGGE"u8.CopyTo(header.AsSpan(0x1FC));
        output.Write(header);

        foreach (var (start, num) in ranges)
        {
            for (var block = start; block < start + num; block++)
            {
                var encrypted = new byte[0x8000];
                var iv = new byte[16];
                WriteBe64(iv, 8, block);
                using var encryptor = aes.CreateEncryptor(aes.Key, iv);
                encryptor.TransformBlock(decoded, (int)block * 0x8000, 0x8000, encrypted, 0);
                output.Write(encrypted);
            }
        }

        return (output.ToArray(), decoded);
    }

    private static void WriteLe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteLe64(Stream stream, ulong value)
    {
        WriteLe32(stream, (uint)value);
        WriteLe32(stream, (uint)(value >> 32));
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteBe64(byte[] data, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            data[offset + i] = (byte)(value >> (56 - 8 * i));
        }
    }
}
