namespace RVZSharp.Models;

/// <summary>Compression methods used by WIA/RVZ (Dolphin: WIARVZCompressionType).</summary>
public enum CompressionType : uint
{
    /// <summary>No compression; the data is stored as is.</summary>
    None = 0,

    /// <summary>WIA only; not supported in RVZ.</summary>
    Purge = 1,

    /// <summary>Bzip2 compression.</summary>
    Bzip2 = 2,

    /// <summary>LZMA compression.</summary>
    Lzma = 3,

    /// <summary>LZMA2 compression.</summary>
    Lzma2 = 4,

    /// <summary>Zstandard compression.</summary>
    Zstd = 5
}
