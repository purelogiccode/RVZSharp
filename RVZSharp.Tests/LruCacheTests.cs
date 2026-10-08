using RVZSharp.IO;

namespace RVZSharp.Tests;

/// <summary>Tests for the internal <see cref="LruCache{TKey, TValue}"/> used by <see cref="RvzReader"/>.</summary>
public class LruCacheTests
{
    /// <summary>Verifies that get or add caches values.</summary>
    [Fact]
    public void GetOrAdd_CachesValues()
    {
        var cache = new LruCache<int, byte[]>(1024);
        var calls = 0;

        var first = cache.GetOrAdd(1, _ =>
        {
            calls++;
            return new byte[100];
        }, value => value.Length);
        var second = cache.GetOrAdd(1, _ =>
        {
            calls++;
            return new byte[100];
        }, value => value.Length);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Count);
    }

    /// <summary>Verifies that get or add evicts least recently used.</summary>
    [Fact]
    public void GetOrAdd_EvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<int, byte[]>(100);
        var a = cache.GetOrAdd(1, _ => new byte[60], value => value.Length);
        cache.GetOrAdd(2, _ => new byte[60], value => value.Length); // evicts 1

        var calls = 0;
        var aAgain = cache.GetOrAdd(1, _ =>
        {
            calls++;
            return new byte[60];
        }, value => value.Length);

        Assert.Equal(1, calls);
        Assert.NotSame(a, aAgain);
    }

    /// <summary>Verifies that get or add hit refreshes recency.</summary>
    [Fact]
    public void GetOrAdd_HitRefreshesRecency()
    {
        var cache = new LruCache<int, byte[]>(100);
        cache.GetOrAdd(1, _ => new byte[60], value => value.Length);
        cache.GetOrAdd(2, _ => new byte[40], value => value.Length);

        // Touch 1 so 2 becomes the least recently used entry.
        cache.GetOrAdd(1, _ => new byte[60], value => value.Length);
        cache.GetOrAdd(3, _ => new byte[40], value => value.Length); // evicts 2, not 1

        // 1 is still cached (the hit refreshed its recency)...
        var oneCalls = 0;
        cache.GetOrAdd(1, _ =>
        {
            oneCalls++;
            return new byte[60];
        }, value => value.Length);
        Assert.Equal(0, oneCalls);

        // ...and 2 was the eviction victim.
        var calls = 0;
        cache.GetOrAdd(2, _ =>
        {
            calls++;
            return new byte[40];
        }, value => value.Length);
        Assert.Equal(1, calls);
    }

    /// <summary>Verifies that get or add oversized value is returned but not cached.</summary>
    [Fact]
    public void GetOrAdd_OversizedValue_IsReturnedButNotCached()
    {
        var cache = new LruCache<int, byte[]>(100);
        var calls = 0;

        for (var i = 0; i < 2; i++)
        {
            cache.GetOrAdd(1, _ =>
            {
                calls++;
                return new byte[200];
            }, value => value.Length);
        }

        Assert.Equal(2, calls);
        Assert.Equal(0, cache.Count);
    }

    /// <summary>Verifies that get or add concurrent misses return valid values and stay bounded.</summary>
    [Fact]
    public void GetOrAdd_ConcurrentMisses_ReturnValidValuesAndStayBounded()
    {
        var cache = new LruCache<int, byte[]>(10 * 100);
        var failures = 0;

        Parallel.For(0, 1000, i =>
        {
            var key = i % 50;
            var value = cache.GetOrAdd(key, k => new byte[100 + k], v => v.Length);
            if (value.Length != 100 + key)
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
        Assert.InRange(cache.Count, 0, 10);
    }

    /// <summary>Verifies that clear drops everything.</summary>
    [Fact]
    public void Clear_DropsEverything()
    {
        var cache = new LruCache<int, byte[]>(1024);
        cache.GetOrAdd(1, _ => new byte[10], value => value.Length);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
