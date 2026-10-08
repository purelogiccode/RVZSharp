namespace RVZSharp.IO;

/// <summary>
/// Thread-safe LRU cache with byte-size accounting. Values are produced outside the lock
/// (a concurrent miss may decode the same key twice; the first insert wins). Values larger
/// than the capacity are returned but not cached.
/// </summary>
/// <typeparam name="TKey">Cache key type.</typeparam>
/// <typeparam name="TValue">Cached value type.</typeparam>
internal sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly record struct Entry(TKey Key, TValue Value, int Size);

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = [];
    private readonly long _maxSize;
    private long _size;

    /// <summary>Creates a cache holding at most <paramref name="maxSize"/> accounted bytes.</summary>
    /// <param name="maxSize">Capacity in bytes (as reported by the value-size selector).</param>
    public LruCache(long maxSize)
    {
        _maxSize = maxSize;
    }

    /// <summary>Number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, producing it with
    /// <paramref name="factory"/> on a miss. <paramref name="sizeOf"/> reports the value's
    /// accounted size in bytes (values above the capacity are returned uncached).
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, Func<TValue, int> sizeOf)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Value;
            }
        }

        var value = factory(key);
        var size = sizeOf(value);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                // Another thread produced the same key first; prefer the cached value.
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Value;
            }

            if (size > _maxSize)
            {
                return value;
            }

            var node = _order.AddFirst(new Entry(key, value, size));
            _map[key] = node;
            _size += size;
            while (_size > _maxSize && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _size -= last.Value.Size;
            }

            return value;
        }
    }

    /// <summary>Drops every cached entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _size = 0;
        }
    }
}
