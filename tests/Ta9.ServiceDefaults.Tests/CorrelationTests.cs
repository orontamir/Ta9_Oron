namespace Ta9.ServiceDefaults.Tests;

public class CorrelationTests
{
    [Theory]
    [InlineData("abc-123")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("svc_1.req-2")]
    public void IsValid_accepts_safe_ids(string id) =>
        Correlation.IsValid(id).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public void IsValid_rejects_unsafe_ids(string? id) =>
        Correlation.IsValid(id).Should().BeFalse();

    [Fact]
    public void IsValid_rejects_ids_longer_than_64_chars() =>
        Correlation.IsValid(new string('a', 65)).Should().BeFalse();

    [Fact]
    public void BeginScope_sets_and_restores_current_id()
    {
        Correlation.CurrentId.Should().BeNull();

        using (Correlation.BeginScope("outer"))
        {
            Correlation.CurrentId.Should().Be("outer");
            using (Correlation.BeginScope("inner"))
            {
                Correlation.CurrentId.Should().Be("inner");
            }
            Correlation.CurrentId.Should().Be("outer");
        }

        Correlation.CurrentId.Should().BeNull();
    }

    [Fact]
    public async Task Current_id_flows_across_awaits()
    {
        using (Correlation.BeginScope("flowing"))
        {
            await Task.Yield();
            var seenInTask = await Task.Run(() => Correlation.CurrentId);
            seenInTask.Should().Be("flowing");
        }
    }
}
