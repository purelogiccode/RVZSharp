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
}