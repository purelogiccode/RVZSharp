namespace RVZSharp;

/// <summary>The file uses a feature this library does not support (e.g. WIA, future versions).</summary>
public sealed class RvzUnsupportedException : RvzException
{
    /// <summary>Creates a new exception with the given message.</summary>
    /// <param name="message">The error message.</param>
    public RvzUnsupportedException(string message)
        : base(message)
    {
    }
}