namespace RVZSharp;

/// <summary>A SHA-1 integrity check stored in the file did not match the actual contents.</summary>
public sealed class RvzHashMismatchException : RvzException
{
    public RvzHashMismatchException(string message)
        : base(message)
    {
    }
}