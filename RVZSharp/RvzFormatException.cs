namespace RVZSharp;

/// <summary>The file is not structurally valid (bad magic, truncated data, inconsistent sizes).</summary>
public sealed class RvzFormatException : RvzException
{
    /// <summary>Creates a new exception with the given message.</summary>
    /// <param name="message">The error message.</param>
    public RvzFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new exception with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The inner exception that caused this one.</param>
    public RvzFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}