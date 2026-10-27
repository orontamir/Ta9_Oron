using Ta9.ServiceDefaults.Security;

namespace Ta9.ServiceDefaults.Tests.Security;

public class ApiKeyRegistryTests
{
    private static readonly string ReaderKey = new('r', 40);
    private static readonly string OperatorKey = new('o', 40);

    private static ApiKeyRegistry Registry(ApiKeyOptions? options = null, params Caller[] accepted) =>
        new(options ?? new ApiKeyOptions { ReaderKey = ReaderKey, OperatorKey = OperatorKey },
            accepted.Length > 0 ? accepted : [Caller.Reader, Caller.Operator]);

    [Fact]
    public void Finds_the_caller_of_a_configured_key()
    {
        var registry = Registry();

        registry.Find(ReaderKey)!.Name.Should().Be("reader");
        registry.Find(OperatorKey)!.Role.Should().Be(Roles.Operator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    public void Unknown_or_empty_key_is_rejected(string? presented)
    {
        Registry().Find(presented).Should().BeNull();
    }

    [Fact]
    public void Caller_without_a_configured_key_never_matches()
    {
        var registry = Registry(new ApiKeyOptions { ReaderKey = "", OperatorKey = OperatorKey });

        registry.Find("").Should().BeNull();
        registry.Missing.Select(c => c.Name).Should().Equal("reader");
    }

    [Fact]
    public void Only_accepted_callers_are_recognized()
    {
        var registry = Registry(new ApiKeyOptions { ReaderKey = ReaderKey, OperatorKey = OperatorKey }, Caller.Reader);

        registry.Find(OperatorKey).Should().BeNull("this service does not trust the operator");
        registry.Accepted.Select(c => c.Name).Should().Equal("reader");
    }

    [Fact]
    public void Reports_keys_that_are_too_short()
    {
        var registry = Registry(new ApiKeyOptions { ReaderKey = "short", OperatorKey = OperatorKey });

        registry.TooShort.Select(c => c.Name).Should().Equal("reader");
        registry.Find("short").Should().NotBeNull("a short key still works; it is only reported");
    }
}
