namespace RVZSharp.Benchmarks;

/// <summary>
/// Builds the synthetic GameCube disc image used by every benchmark: 16 MiB by default, half
/// random data and a quarter zeroes (so both the literal and the zero-group paths are
/// exercised), with the GC disc magic at 0x1C so the writers accept it.
/// </summary>
internal static class BenchmarkDisc
{
    public const int DefaultSize = 16 * 1024 * 1024;

    public static byte[] BuildGameCubeImage(int size = DefaultSize)
    {
        var iso = new byte[size];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2; // GC DVD magic (0xC2339F3D)
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        Array.Clear(iso, size / 2, size / 4); // zero region
        return iso;
    }
}
