using CloudHttp.Internal;

namespace CloudHttp.Tests.Internal;

public class RoundRobinSelectorTests
{
    [Fact]
    public void Cycles_through_clients_in_order()
    {
        var s = new RoundRobinSelector(4);

        var picks = Enumerable.Range(0, 8).Select(_ => s.Select(null)).ToArray();

        picks.Should().Equal(0, 1, 2, 3, 0, 1, 2, 3);
    }

    [Fact]
    public void Distributes_evenly_over_many_calls()
    {
        var s = new RoundRobinSelector(4);
        var counts = new int[4];

        for (var i = 0; i < 4000; i++) counts[s.Select(null)]++;

        counts.Should().AllSatisfy(c => c.Should().Be(1000));
    }

    [Fact]
    public void Avoids_previous_index_when_rotating()
    {
        var s = new RoundRobinSelector(4);

        // first pick brings idx to 0
        var first = s.Select(null);
        var rotated = s.Select(previousIndex: first);

        rotated.Should().NotBe(first);
    }

    [Fact]
    public void Single_client_returns_zero_even_for_rotation()
    {
        var s = new RoundRobinSelector(1);

        var first = s.Select(null);
        var rotated = s.Select(first);

        first.Should().Be(0);
        rotated.Should().Be(0);
    }

    [Fact]
    public void Throws_on_zero_count()
    {
        var act = () => new RoundRobinSelector(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
