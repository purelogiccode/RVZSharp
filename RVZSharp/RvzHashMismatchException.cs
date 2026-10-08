namespace RVZSharp;

/// <summary>A SHA-1 integrity check stored in the file did not match the actual contents.</summary>
public sealed class RvzHashMismatchException : RvzException
{
    /// <summary>Creates a new exception with the given message.</summary>
    /// <param name="message">The error message.</param>
    public RvzHashMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new exception with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The inner exception that caused this one.</param>
    public RvzHashMismatchException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates a new exception with no message.</summary>
    public RvzHashMismatchException()
    {
    }
}
