namespace RVZSharp.Format;

/// <summary>The two container formats sharing the WIA/RVZ core (Dolphin: WIARVZFileReader&lt;RVZ&gt;).</summary>
public enum WiaRvzFormat
{
    /// <summary>Wii ISO Archive (magic "WIA\x01", 8-byte group entries, no packing, no Zstandard).</summary>
    Wia,

    /// <summary>Dolphin RVZ (magic "RVZ\x01", 12-byte group entries, RVZ packing, Zstandard).</summary>
    Rvz,
}
