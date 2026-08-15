using RVZSharp.Packing;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// RvzPackingEncoder: pack a chunk with embedded junk and decode it back with the reader's
/// RvzPackingDecoder (which reconstructs junk from the recovered seed).
/// </summary>
public class RvzPackingEncoderTests
{
    [Fact]
    public void Pack_WithJunk_RoundTrips()
    {
        var seed = new byte[68];
        new Random(3).NextBytes(seed);
        var payload = new byte[0x20000];
        new Random(7).NextBytes(payload);
        var junk = ReferencePrng.Generate(seed, 0x8000, 0x8000);
        junk.CopyTo(payload, 0x8000);

        AssertRoundTrip(payload, dataOffset: 0x8000);
    }

    [Fact]
    public void Pack_WithJunkAtUnalignedOffset_RoundTrips()
    {
        var seed = new byte[68];
        new Random(3).NextBytes(seed);
        var payload = new byte[0x20000];
        var junk = ReferencePrng.Generate(seed, 0x1234, 0x18000);
        junk.CopyTo(payload, 0x1234);

        AssertRoundTrip(payload, dataOffset: 0x8000);
    }

    [Fact]
    public void Pack_NoJunk_StoresChunkWithoutHeaders()
    {
        var payload = new byte[0x20000];
        new Random(7).NextBytes(payload);

        var mainData = new List<byte>();
        uint packedSize = 0;
        RvzPackingEncoder.Pack(payload, dataOffset: 0, bytesPerChunk: payload.Length, chunks: 1,
            allowJunkReuse: true, compression: true, mainData, ref packedSize);

        // No junk found: the whole chunk is stored literally, without size headers.
        Assert.Equal(0u, packedSize);
        Assert.Equal(payload, mainData);
    }

    [Fact]
    public void Pack_AllZeroes_StoresZeroJunkWhenUncompressed()
    {
        var payload = new byte[0x20000];

        var mainData = new List<byte>();
        uint packedSize = 0;
        RvzPackingEncoder.Pack(payload, dataOffset: 0, bytesPerChunk: payload.Length, chunks: 1,
            allowJunkReuse: true, compression: false, mainData, ref packedSize);

        // The whole chunk becomes one zero-junk segment: size header + 68-byte seed.
        Assert.Equal(4u + 68, packedSize);
        Assert.Equal(4 + 68, mainData.Count);
        var header = (uint)((mainData[0] << 24) | (mainData[1] << 16) | (mainData[2] << 8) | mainData[3]);
        Assert.Equal(0x8000_0000u | 0x20000u, header);
    }

    [Fact]
    public void Pack_MultipartChunks_AlwaysWriteSizeHeaders()
    {
        // Dolphin disables the headerless fast path for multipart entries
        // (first_loop_iteration = !multipart, WIABlob.cpp:1237): every chunk gets a
        // segment stream, even without junk.
        var payload = new byte[0x20000];
        new Random(7).NextBytes(payload);

        var mainData = new List<byte>();
        uint packedSize = 0;
        RvzPackingEncoder.Pack(payload, dataOffset: 0, bytesPerChunk: payload.Length / 2, chunks: 2,
            allowJunkReuse: true, compression: true, mainData, ref packedSize);

        // Two literal segments with 4-byte headers each.
        Assert.Equal(2u * 4 + (uint)payload.Length, packedSize);
        Assert.Equal(2 * 4 + payload.Length, mainData.Count);
        var header0 = (uint)((mainData[0] << 24) | (mainData[1] << 16) | (mainData[2] << 8) | mainData[3]);
        Assert.Equal((uint)(payload.Length / 2), header0);

        // And the segment stream still decodes to the payload.
        using var stream = new MemoryStream([.. mainData], writable: false);
        using var decoder = new RvzPackingDecoder(stream, dataOffset: 0);
        var decoded = new byte[payload.Length];
        var read = 0;
        while (read < decoded.Length)
        {
            var take = decoder.Read(decoded, read, decoded.Length - read);
            Assert.True(take > 0, $"decoder stalled at {read}");
            read += take;
        }

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void TruncatedSizeHeader_Throws()
    {
        // A partial 1-3 byte segment header at EOF is corruption, not a clean end
        // (Dolphin fails; Go returns ErrUnexpectedEOF).
        using var input = new MemoryStream([0x00, 0x01]);
        using var decoder = new RvzPackingDecoder(input, dataOffset: 0);
        var buffer = new byte[16];
        Assert.Throws<RvzFormatException>(() => decoder.Read(buffer, 0, buffer.Length));
    }

    private static void AssertRoundTrip(byte[] payload, long dataOffset)
    {
        var mainData = new List<byte>();
        uint packedSize = 0;
        RvzPackingEncoder.Pack(payload, dataOffset, bytesPerChunk: payload.Length, chunks: 1,
            allowJunkReuse: true, compression: true, mainData, ref packedSize);
        Assert.True(packedSize > 0);

        using var stream = new MemoryStream([.. mainData], writable: false);
        using var decoder = new RvzPackingDecoder(stream, dataOffset);
        var decoded = new byte[payload.Length];
        var read = 0;
        while (read < decoded.Length)
        {
            var take = decoder.Read(decoded, read, decoded.Length - read);
            Assert.True(take > 0, $"decoder stalled at {read}");
            read += take;
        }

        Assert.Equal(payload, decoded);
    }
}
