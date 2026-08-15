namespace RVZSharp.Blobs;

/// <summary>The container formats detected by <see cref="Blob.Open(Stream, string?, bool)"/> (Dolphin: BlobType).</summary>
public enum BlobType
{
    /// <summary>A plain, uncompressed disc image (ISO).</summary>
    Plain,

    /// <summary>Dolphin GameCube Zip (zlib-compressed 16 KiB blocks).</summary>
    Gcz,

    /// <summary>Compact ISO (fixed blocks with a presence map).</summary>
    Ciso,

    /// <summary>Wii Backup File System (cluster allocation table).</summary>
    Wbfs,

    /// <summary>GameCube TGC (header + relocated DOL/FST).</summary>
    Tgc,

    /// <summary>Wii ISO Archive.</summary>
    Wia,

    /// <summary>Dolphin RVZ.</summary>
    Rvz,

    /// <summary>Wii U eShop NFS (EGGS, AES-encrypted blocks).</summary>
    Nfs,
}
