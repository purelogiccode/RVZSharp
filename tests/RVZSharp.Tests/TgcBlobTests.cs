using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class TgcBlobTests
{
    private static byte[] ReadAll(TgcBlob reader)
    {
        var output = new byte[reader.Length];
        var position = 0;
        while (position < output.Length)
        {
            var read = reader.ReadAt(position, output.AsSpan(position));
            Assert.True(read > 0);
            position += read;
        }

        return output;
    }

    [Fact]
    public void RoundTrip_RelocatesDolFstAndFstOffsets()
    {
        var (tgc, iso) = TestLegacyBuilders.BuildTgc();

        using var reader = TgcBlob.Open(new MemoryStream(tgc));
        Assert.Equal(BlobType.Tgc, reader.Type);
        Assert.Equal(iso.Length, reader.Length);
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void RoundTrip_NoDolRelocation()
    {
        // A file where the DOL sits exactly at tgc_header_size, so its relocated offset is 0.
        var (tgc, iso) = TestLegacyBuilders.BuildTgc(dolReal: 0x100);

        using var reader = TgcBlob.Open(new MemoryStream(tgc));
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void RandomAccess_AcrossPatchedRegions()
    {
        var (tgc, iso) = TestLegacyBuilders.BuildTgc();

        using var reader = TgcBlob.Open(new MemoryStream(tgc));
        // Read a range that starts inside the FST replacement and extends past it.
        var start = 0x100;
        var length = 0x60;
        var probe = new byte[length];
        reader.ReadAt(start, probe);
        Assert.Equal(iso.AsSpan(start, length).ToArray(), probe);
    }

    [Fact]
    public void BadMagic_ThrowsFormatException()
    {
        var bytes = new byte[64];
        Assert.Throws<RvzFormatException>(() => TgcBlob.Open(new MemoryStream(bytes)));
    }
}
