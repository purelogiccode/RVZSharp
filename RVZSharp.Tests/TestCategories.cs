namespace RVZSharp.Tests;

/// <summary>
/// xUnit trait categories for splitting the suite into a fast subset and the full run.
/// Long-running tests (decoding real disc images from disk, byte-exact comparisons)
/// are marked with <see cref="Category"/> = <see cref="Slow"/>.
/// </summary>
public static class TestCategories
{
    public const string Category = "Category";
    public const string Slow = "Slow";
}