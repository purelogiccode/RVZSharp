namespace RVZSharp.Format;

/// <summary>Compression methods used by WIA/RVZ (Dolphin: WIARVZCompressionType).</summary>
public enum CompressionType : uint
{
    None = 0,

    /// <summary>WIA only; not supported in RVZ.</summary>
    Purge = 1,
    Bzip2 = 2,
    Lzma = 3,
    Lzma2 = 4,
    Zstd = 5,
}
