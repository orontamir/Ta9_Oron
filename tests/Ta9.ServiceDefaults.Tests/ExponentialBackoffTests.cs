namespace Ta9.ServiceDefaults.Tests;

public class ExponentialBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Grows_exponentially_up_to_the_cap_with_jitter(int attempt, double expectedSeconds)
    {
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            var delay = ExponentialBackoff.Compute(attempt, Initial, Max, random);

            delay.TotalSeconds.Should().BeInRange(expectedSeconds * 0.8, expectedSeconds * 1.2);
        }
    }

    [Fact]
    public void Jitter_spreads_retries()
    {
        var delays = Enumerable.Range(0, 50).Select(_ => ExponentialBackoff.Compute(3, Initial, Max)).Distinct();

        delays.Should().HaveCountGreaterThan(1);
    }
}
