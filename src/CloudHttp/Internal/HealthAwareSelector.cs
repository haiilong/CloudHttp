namespace CloudHttp.Internal;

internal sealed class HealthAwareSelector : IClientSelector
{
    private readonly long[] _degradedUntilTicks;
    private readonly long _timeoutMs;
    private readonly Func<long> _now;
    private long _rrIdx = -1;

    public int Count { get; }

    public HealthAwareSelector(int count, TimeSpan degradedTimeout, Func<long>? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        Count = count;
        _degradedUntilTicks = new long[count];
        _timeoutMs = (long)degradedTimeout.TotalMilliseconds;
        _now = now ?? (static () => Environment.TickCount64);
    }

    public int Select(int? previousIndex)
    {
        var now = _now();
        var start = (int)((Interlocked.Increment(ref _rrIdx) & long.MaxValue) % Count);

        for (var i = 0; i < Count; i++)
        {
            var idx = (start + i) % Count;
            if (idx == previousIndex) continue;
            if (Volatile.Read(ref _degradedUntilTicks[idx]) <= now) return idx;
        }

        for (var i = 0; i < Count; i++)
        {
            var idx = (start + i) % Count;
            if (idx != previousIndex) return idx;
        }

        return start;
    }

    public void MarkDegraded(int index)
    {
        if ((uint)index >= (uint)Count) return;
        Volatile.Write(ref _degradedUntilTicks[index], _now() + _timeoutMs);
    }

    public void MarkHealthy(int index)
    {
        if ((uint)index >= (uint)Count) return;
        // Recovery is timeout-based. Clearing on success can let an older successful
        // request erase a newer degradation recorded by a concurrent failed request.
    }
}
