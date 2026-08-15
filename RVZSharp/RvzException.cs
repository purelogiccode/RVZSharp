namespace RVZSharp;

/// <summary>Base class for all RVZSharp errors.</summary>
public class RvzException : Exception
{
    /// <summary>Creates a new exception with the given message.</summary>
    /// <param name="message">The error message.</param>
    public RvzException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new exception with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The inner exception that caused this one.</param>
    public RvzException(string message, Exception inner)
        : base(message, inner)
    {
    }
}