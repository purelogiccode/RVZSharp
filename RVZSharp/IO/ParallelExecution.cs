using System.Runtime.ExceptionServices;

namespace RVZSharp.IO;

/// <summary>
/// Runs <see cref="Parallel"/> loops while preserving the exception thrown by the loop body:
/// <see cref="Parallel"/> wraps failures in an <see cref="AggregateException"/>, but the
/// library's documented failure mode is the original exception (e.g. <see cref="RvzFormatException"/>).
/// </summary>
internal static class ParallelExecution
{
    /// <summary>Runs a <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> loop.</summary>
    /// <param name="fromInclusive">The start index.</param>
    /// <param name="toExclusive">The exclusive end index.</param>
    /// <param name="options">Parallel options (degree of parallelism and cancellation).</param>
    /// <param name="body">The loop body.</param>
    public static void For(int fromInclusive, int toExclusive, ParallelOptions options,
        Action<int> body)
    {
        try
        {
            Parallel.For(fromInclusive, toExclusive, options, body);
        }
        catch (AggregateException e)
        {
            Rethrow(e);
        }
    }

    /// <summary>Runs a thread-local <see cref="Parallel.For{TLocal}(int, int, ParallelOptions, Func{TLocal}, Func{int, ParallelLoopState, TLocal, TLocal}, Action{TLocal})"/> loop.</summary>
    /// <typeparam name="TLocal">The per-thread state type.</typeparam>
    /// <param name="fromInclusive">The start index.</param>
    /// <param name="toExclusive">The exclusive end index.</param>
    /// <param name="options">Parallel options (degree of parallelism and cancellation).</param>
    /// <param name="localInit">Creates the per-thread state.</param>
    /// <param name="body">The loop body.</param>
    /// <param name="localFinally">Releases the per-thread state.</param>
    public static void For<TLocal>(int fromInclusive, int toExclusive, ParallelOptions options,
        Func<TLocal> localInit, Func<int, ParallelLoopState, TLocal, TLocal> body,
        Action<TLocal> localFinally)
    {
        try
        {
            Parallel.For(fromInclusive, toExclusive, options, localInit, body, localFinally);
        }
        catch (AggregateException e)
        {
            Rethrow(e);
        }
    }

    private static void Rethrow(AggregateException e)
    {
        var inners = e.Flatten().InnerExceptions;
        // Prefer the real loop-body failure over a concurrent cancellation so the documented
        // exception type survives when both are present.
        var inner = inners.FirstOrDefault(ex => ex is not OperationCanceledException)
                    ?? inners.FirstOrDefault();
        if (inner is null)
        {
            throw e;
        }

        ExceptionDispatchInfo.Capture(inner).Throw();
    }
}
