namespace RVZSharp;

/// <summary>The file uses a feature this library does not support (e.g. PURGE inside RVZ,
/// Zstandard inside WIA, future format versions).</summary>
public sealed class RvzUnsupportedException : RvzException
{
    /// <summary>Creates a new exception with the given message.</summary>
    /// <param name="message">The error message.</param>
    public RvzUnsupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new exception with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The inner exception that caused this one.</param>
    public RvzUnsupportedException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates a new exception with no message.</summary>
    public RvzUnsupportedException()
    {
    }
}
