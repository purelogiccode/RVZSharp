namespace RVZSharp;

/// <summary>Base class for all RVZSharp errors.</summary>
public class RvzException : Exception
{
    public RvzException(string message)
        : base(message) { }

    public RvzException(string message, Exception inner)
        : base(message, inner) { }
}