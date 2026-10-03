namespace SampleConcurrencyLib;

/// <summary>
/// C29 fixture lock primitives (design §7.2): nested lock regions and
/// same-thread recursive re-entry. The lock statements are the CN-1 direct
/// mapping — the pass must never rewrite them, only count and audit them.
/// </summary>
public sealed class Counter
{
    private readonly object _primary = new();
    private readonly object _secondary = new();
    private int _nestedCount;
    private int _currentReentryDepth;
    private int _maxReentryDepth;
    private int _leafCalls;

    public int NestedCount => _nestedCount;

    public int MaxReentryDepth => _maxReentryDepth;

    public int LeafCalls => _leafCalls;

    /// <summary>Nested lock regions: the inner lock is the nested region (CN-1).</summary>
    public void IncrementNested()
    {
        lock (_primary)
        {
            lock (_secondary)
            {
                _nestedCount++;
            }
        }
    }

    /// <summary>
    /// Same-thread recursive re-entry on one monitor object (CN-12): the
    /// recursive call re-acquires the lock the calling thread already holds.
    /// </summary>
    public void RecursiveReentry(int depth)
    {
        lock (_primary)
        {
            _currentReentryDepth++;
            if (_currentReentryDepth > _maxReentryDepth)
            {
                _maxReentryDepth = _currentReentryDepth;
            }

            try
            {
                if (depth > 0)
                {
                    RecursiveReentry(depth - 1);
                }
                else
                {
                    _leafCalls++;
                }
            }
            finally
            {
                _currentReentryDepth--;
            }
        }
    }
}
