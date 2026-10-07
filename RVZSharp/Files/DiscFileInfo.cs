namespace RVZSharp.Files;

/// <summary>
/// One file or directory in a disc's file system table (Dolphin: FileInfoGCWii). Offsets are
/// relative to the partition's decrypted data area (GameCube: the disc itself).
/// </summary>
public sealed class DiscFileInfo
{
    private readonly List<DiscFileInfo> _children = [];

    internal DiscFileInfo(string name, bool isDirectory, long offset, long size,
        DiscFileInfo? parent)
    {
        Name = name;
        IsDirectory = isDirectory;
        Offset = offset;
        Size = size;
        Parent = parent;
        Path = parent is null
            ? string.Empty
            : parent.Path + name + (isDirectory ? "/" : string.Empty);
    }

    /// <summary>The entry name (empty for the root).</summary>
    public string Name { get; }

    /// <summary>
    /// The path from the file-system root (empty for the root, directories end with '/',
    /// e.g. <c>files/maps/foo.dat</c>).
    /// </summary>
    public string Path { get; }

    /// <summary>True when this entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>True for the root entry.</summary>
    public bool IsRoot => Parent is null;

    /// <summary>The data offset in the partition's decrypted view (0 for directories).</summary>
    public long Offset { get; }

    /// <summary>The file size in bytes (for directories, the index of the entry after its subtree).</summary>
    public long Size { get; }

    /// <summary>The parent directory (null for the root).</summary>
    public DiscFileInfo? Parent { get; }

    /// <summary>The direct children of this directory (empty for files).</summary>
    public IReadOnlyList<DiscFileInfo> Children => _children;

    /// <summary>Adds a child while the tree is being built.</summary>
    internal void AddChild(DiscFileInfo child)
    {
        _children.Add(child);
    }
}
