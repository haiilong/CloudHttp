using CloudHttp.Internal;

namespace CloudHttp.Tests.Internal;

public class HealthAwareSelectorTests
{
    [Fact]
    public void Skips_degraded_index_until_timeout_elapses()
    {
        var clock = new TestClock { Now = 1000 };
        var s = new HealthAwareSelector(3, TimeSpan.FromMilliseconds(500), () => clock.Now);

        s.MarkDegraded(1);

        var picks = new HashSet<int>();
        for (var i = 0; i < 20; i++) picks.Add(s.Select(null));
        picks.Should().BeEquivalentTo(new[] { 0, 2 }); // idx 1 skipped

        clock.Now = 1500;
        // selector uses `degradedUntil <= now` so at the exact tick the slot is healthy again
        picks.Clear();
        for (var i = 0; i < 20; i++) picks.Add(s.Select(null));
        picks.Should().Contain(1); // back in rotation
    }

    [Fact]
    public void MarkHealthy_does_not_restore_index_before_timeout()
    {
        var clock = new TestClock { Now = 1000 };
        var s = new HealthAwareSelector(2, TimeSpan.FromSeconds(10), () => clock.Now);

        s.MarkDegraded(0);
        var picks = new HashSet<int>();
        for (var i = 0; i < 10; i++) picks.Add(s.Select(null));
        picks.Should().BeEquivalentTo(new[] { 1 });

        s.MarkHealthy(0);
        picks.Clear();
        for (var i = 0; i < 10; i++) picks.Add(s.Select(null));
        picks.Should().BeEquivalentTo(new[] { 1 });

        clock.Now = 11_000;
        picks.Clear();
        for (var i = 0; i < 10; i++) picks.Add(s.Select(null));
        picks.Should().Contain(0);
    }

    [Fact]
    public void Falls_back_to_round_robin_when_all_degraded()
    {
        long now = 1000;
        var s = new HealthAwareSelector(3, TimeSpan.FromSeconds(10), () => now);

        s.MarkDegraded(0);
        s.MarkDegraded(1);
        s.MarkDegraded(2);

        var picks = new HashSet<int>();
        for (var i = 0; i < 30; i++) picks.Add(s.Select(null));

        picks.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public void Avoids_previous_index_when_rotating()
    {
        long now = 1000;
        var s = new HealthAwareSelector(3, TimeSpan.FromSeconds(10), () => now);

        for (var i = 0; i < 10; i++)
        {
            var first = s.Select(null);
            var rotated = s.Select(previousIndex: first);
            rotated.Should().NotBe(first);
        }
    }

    [Fact]
    public void Out_of_range_mark_is_noop()
    {
        var s = new HealthAwareSelector(2, TimeSpan.FromSeconds(1));
        var act = () => { s.MarkDegraded(99); s.MarkHealthy(-1); };
        act.Should().NotThrow();
    }

    private sealed class TestClock
    {
        public long Now { get; set; }
    }
}
