namespace CloudHttp.Internal;

internal sealed class RoundRobinSelector : IClientSelector
{
    private long _idx = -1;

    public int Count { get; }

    public RoundRobinSelector(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Count = count;
    }

    public int Select(int? previousIndex)
    {
        var next = (int)((Interlocked.Increment(ref _idx) & long.MaxValue) % Count);
        if (previousIndex == next && Count > 1)
        {
            next = (int)((Interlocked.Increment(ref _idx) & long.MaxValue) % Count);
        }
        return next;
    }

    public void MarkDegraded(int index) { }
    public void MarkHealthy(int index) { }
}
