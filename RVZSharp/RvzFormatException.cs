namespace RVZSharp;

/// <summary>The file is not structurally valid (bad magic, truncated data, inconsistent sizes).</summary>
public sealed class RvzFormatException : RvzException
{
    public RvzFormatException(string message)
        : base(message)
    {
    }

    public RvzFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}