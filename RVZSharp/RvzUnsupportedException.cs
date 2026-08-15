namespace RVZSharp;

/// <summary>The file uses a feature this library does not support (e.g. WIA, future versions).</summary>
public sealed class RvzUnsupportedException : RvzException
{
    public RvzUnsupportedException(string message)
        : base(message)
    {
    }
}