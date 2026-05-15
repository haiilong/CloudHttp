namespace CloudHttp.Internal;

internal sealed class WeightedSelector : IClientSelector
{
    private readonly (double UpperExclusive, int ClientIndex)[] _ladder;
    private readonly double _totalWeight;
    private readonly Func<double> _rng;

    public int Count { get; }

    public WeightedSelector(int count, IReadOnlyDictionary<int, double> weights, Func<double>? rng = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentNullException.ThrowIfNull(weights);

        Count = count;
        _rng = rng ?? Random.Shared.NextDouble;

        var ordered = weights
            .Where(kv => kv.Key >= 0 && kv.Key < count && kv.Value > 0)
            .OrderBy(kv => kv.Key)
            .ToArray();

        if (ordered.Length == 0)
            throw new ArgumentException("No positive weights within client index range.", nameof(weights));

        _ladder = new (double, int)[ordered.Length];
        double cumulative = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            cumulative += ordered[i].Value;
            _ladder[i] = (cumulative, ordered[i].Key);
        }
        _totalWeight = cumulative;
    }

    public int Select(int? previousIndex)
    {
        var idx = PickOne();
        if (previousIndex is { } previous && previous == idx && _ladder.Length > 1)
        {
            for (var i = 0; i < _ladder.Length; i++)
            {
                if (_ladder[i].ClientIndex != previous) return _ladder[i].ClientIndex;
            }
        }
        return idx;
    }

    private int PickOne()
    {
        var target = _rng() * _totalWeight;
        int lo = 0, hi = _ladder.Length - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_ladder[mid].UpperExclusive > target) hi = mid;
            else lo = mid + 1;
        }
        return _ladder[lo].ClientIndex;
    }

    public void MarkDegraded(int index) { }
    public void MarkHealthy(int index) { }
}
