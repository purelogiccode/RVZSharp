using System.Security.Cryptography;
using RVZSharp.Chunks;

namespace RVZSharp.Tests.Helpers;

/// <summary>
/// Independent reference for the Wii partition region rebuild, translated from the Go rvz
/// reader's part.go (writeHashes/encryptSector) which mirrors Dolphin's VolumeWii::HashGroup
/// and EncryptGroup. Zero-fills missing sectors (Dolphin semantics) when realSectors &lt; 64.
/// </summary>
internal static class ReferenceWiiRegion
{
    public const int HashSize = 0x400;
    public const int SectorDataSize = 0x7C00;
    public const int SectorSize = 0x8000;

    public static byte[] Build(byte[] data, byte[] key, HashExceptionEntry[][] sectorExceptions, int realSectors)
    {
        const int clusters = 64;
        var h0 = new byte[clusters][];
        var cluster = new byte[clusters][];

        for (var j = 0; j < clusters; j++)
        {
            h0[j] = new byte[HashSize];
            cluster[j] = new byte[SectorDataSize];
        }

        // H0 hashes per sector (Go readGroup).
        for (var j = 0; j < realSectors; j++)
        {
            var sectorData = data.AsSpan(j * SectorDataSize, SectorDataSize);
            sectorData.CopyTo(cluster[j]);
            for (var k = 0; k < 31; k++)
            {
                var hash = SHA1.HashData(sectorData.Slice(k * 0x400, 0x400));
                hash.CopyTo(h0[j], k * 20);
            }
        }

        // The h0 area of an all-zero sector, from first principles: the zero-filled sector
        // data is hashed normally (Go readGroup reads DevZero; Dolphin zero-fills and hashes),
        // so h0 = 31 × SHA1(0x400 zeros). Sectors beyond realSectors hash to this.
        var zeroBlockHash = SHA1.HashData(new byte[0x400]);
        var zeroSectorH0 = new byte[31 * 20];
        for (var k = 0; k < 31; k++)
        {
            zeroBlockHash.CopyTo(zeroSectorH0, k * 20);
        }

        // H1: h1[i] = SHA1(h0 of sector i), the whole 8-slot array is shared by the 8 sectors.
        for (var i = 0; i < clusters; i++)
        {
            var hash = SHA1.HashData(i < realSectors ? h0[i].AsSpan(0, 31 * 20) : zeroSectorH0);
            for (var s = 0; s < 8; s++)
            {
                hash.CopyTo(h0[(i / 8) * 8 + s], 0x280 + (i % 8) * 20);
            }
        }

        // H2: h2[i] = SHA1(h1 array of group i), shared by all 64 sectors.
        for (var i = 0; i < 8; i++)
        {
            var hash = SHA1.HashData(h0[i * 8].AsSpan(0x280, 8 * 20));
            for (var s = 0; s < clusters; s++)
            {
                hash.CopyTo(h0[s], 0x340 + i * 20);
            }
        }

        var output = new byte[realSectors * SectorSize];
        for (var j = 0; j < realSectors; j++)
        {
            if (sectorExceptions[j].Length > 0)
            {
                foreach (var ex in sectorExceptions[j])
                {
                    ex.Hash.CopyTo(h0[j], ex.Offset & 0x3FF);
                }
            }

            var encryptedHash = AesCbcEncrypt(h0[j], key, new byte[16]);
            encryptedHash.CopyTo(output, j * SectorSize);

            var iv = encryptedHash.AsSpan(0x3D0, 16).ToArray();
            var encryptedData = AesCbcEncrypt(cluster[j], key, iv);
            encryptedData.CopyTo(output, j * SectorSize + HashSize);
        }

        return output;
    }

    private static byte[] AesCbcEncrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
