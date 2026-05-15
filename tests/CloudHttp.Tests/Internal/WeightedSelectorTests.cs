using CloudHttp.Internal;

namespace CloudHttp.Tests.Internal;

public class WeightedSelectorTests
{
    [Fact]
    public void Weight_ratio_holds_over_many_picks()
    {
        var weights = new Dictionary<int, double> { [0] = 1, [1] = 3 };
        var rng = new Random(12345);
        var s = new WeightedSelector(2, weights, rng.NextDouble);
        var counts = new int[2];

        for (var i = 0; i < 40_000; i++) counts[s.Select(null)]++;

        var ratio = (double)counts[1] / counts[0];
        ratio.Should().BeApproximately(3.0, 0.1);
    }

    [Fact]
    public void Picks_correct_bucket_for_deterministic_rng()
    {
        // weights: 0->1, 1->3, 2->6. cumulative: [1, 4, 10]. total=10.
        var weights = new Dictionary<int, double> { [0] = 1, [1] = 3, [2] = 6 };
        var sequence = new Queue<double>(new[] { 0.05, 0.2, 0.5, 0.99 });
        var s = new WeightedSelector(3, weights, () => sequence.Dequeue());

        s.Select(null).Should().Be(0); // 0.05 * 10 = 0.5 < 1 → idx 0
        s.Select(null).Should().Be(1); // 0.2 * 10 = 2 → in (1, 4] → idx 1
        s.Select(null).Should().Be(2); // 0.5 * 10 = 5 → in (4, 10] → idx 2
        s.Select(null).Should().Be(2); // 0.99 * 10 = 9.9 → idx 2
    }

    [Fact]
    public void Rotation_returns_different_index_when_possible()
    {
        var weights = new Dictionary<int, double> { [0] = 1, [1] = 1 };
        var s = new WeightedSelector(2, weights, () => 0.1); // always picks idx 0

        s.Select(null).Should().Be(0);
        s.Select(previousIndex: 0).Should().Be(1);
    }

    [Fact]
    public void Throws_when_no_valid_weights()
    {
        var weights = new Dictionary<int, double> { [5] = 1 };
        var act = () => new WeightedSelector(2, weights);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_on_zero_count()
    {
        var act = () => new WeightedSelector(0, new Dictionary<int, double>());
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
