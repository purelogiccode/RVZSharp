namespace RVZSharp;

/// <summary>Base class for all RVZSharp errors.</summary>
public class RvzException : Exception
{
    public RvzException(string message)
        : base(message) { }

    public RvzException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>The file is not structurally valid (bad magic, truncated data, inconsistent sizes).</summary>
public sealed class RvzFormatException : RvzException
{
    public RvzFormatException(string message)
        : base(message) { }

    public RvzFormatException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>A SHA-1 integrity check stored in the file did not match the actual contents.</summary>
public sealed class RvzHashMismatchException : RvzException
{
    public RvzHashMismatchException(string message)
        : base(message) { }
}

/// <summary>The file uses a feature this library does not support (e.g. WIA, future versions).</summary>
public sealed class RvzUnsupportedException : RvzException
{
    public RvzUnsupportedException(string message)
        : base(message) { }
}
