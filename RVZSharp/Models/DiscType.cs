namespace RVZSharp.Models;

/// <summary>Disc types supported by WIA/RVZ (Dolphin: disc_type in wia_disc_t).</summary>
public enum DiscType : uint
{
    /// <summary>The disc type is unknown or not recognized.</summary>
    Unknown = 0,

    /// <summary>A GameCube disc.</summary>
    GameCube = 1,

    /// <summary>A Wii disc.</summary>
    Wii = 2
}
