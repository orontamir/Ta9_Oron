using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Infrastructure.Processor;

namespace Ta9.JobService.Tests.Infrastructure;

public class HttpProcessorClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://processor/") };
    }

    private static HttpProcessorClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new StubFactory(new StubHandler(respond)), Options.Create(new ProcessorClientOptions()), NullLogger<HttpProcessorClient>.Instance);

    private static HttpProcessorClient Respond(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) =>
        Client(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("details") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    private static readonly IReadOnlyList<ProcessDocumentRequest> Documents = [new(0, "a.txt", "hello")];

    [Theory]
    [InlineData(HttpStatusCode.Accepted, DispatchOutcome.Accepted)]
    [InlineData(HttpStatusCode.OK, DispatchOutcome.Accepted)]
    [InlineData(HttpStatusCode.BadRequest, DispatchOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, DispatchOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, DispatchOutcome.Unreachable)]
    [InlineData(HttpStatusCode.BadGateway, DispatchOutcome.Unreachable)]
    public async Task Submit_maps_status_codes_to_outcomes(HttpStatusCode status, DispatchOutcome expected) =>
        (await Respond(status).SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None)).Outcome.Should().Be(expected);

    [Fact]
    public async Task Submit_503_is_busy_with_the_retry_after_hint()
    {
        var client = Respond(HttpStatusCode.ServiceUnavailable, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));

        var result = await client.SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None);

        result.Outcome.Should().Be(DispatchOutcome.Busy);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Submit_503_without_hint_uses_a_default() =>
        (await Respond(HttpStatusCode.ServiceUnavailable).SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None))
            .RetryAfter.Should().Be(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Submit_rejection_includes_the_response_body() =>
        (await Respond(HttpStatusCode.BadRequest).SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None))
            .Error.Should().Contain("400").And.Contain("details");

    [Fact]
    public async Task Submit_posts_the_contract_payload()
    {
        HttpMethod? method = null;
        string? path = null;
        ProcessJobRequest? body = null;
        var client = Client(async request =>
        {
            method = request.Method;
            path = request.RequestUri!.AbsolutePath;
            body = await request.Content!.ReadFromJsonAsync<ProcessJobRequest>();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var jobId = Guid.NewGuid();

        await client.SubmitAsync(jobId, Documents, CancellationToken.None);

        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/api/process");
        body!.JobId.Should().Be(jobId);
        body.Documents.Should().BeEquivalentTo(Documents);
    }

    [Fact]
    public async Task Submit_connection_failure_is_unreachable() =>
        (await Client(_ => throw new HttpRequestException("Connection refused")).SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None))
            .Outcome.Should().Be(DispatchOutcome.Unreachable);

    [Fact]
    public async Task Submit_timeout_is_unreachable() =>
        (await Client(_ => throw new TaskCanceledException("timeout")).SubmitAsync(Guid.NewGuid(), Documents, CancellationToken.None))
            .Outcome.Should().Be(DispatchOutcome.Unreachable);

    [Fact]
    public async Task Submit_propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Client(_ => throw new TaskCanceledException());

        var act = () => client.SubmitAsync(Guid.NewGuid(), Documents, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Get_maps_found_not_found_and_failures()
    {
        var jobId = Guid.NewGuid();
        var status = new ProcessJobStatusResponse(jobId, ProcessingStatus.Processing, 3, 1, DateTimeOffset.UtcNow, null, null, null, null, null);

        var found = await Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status) }))
            .GetAsync(jobId, CancellationToken.None);
        found.Outcome.Should().Be(LookupOutcome.Found);
        found.Status!.Status.Should().Be(ProcessingStatus.Processing);

        (await Respond(HttpStatusCode.NotFound).GetAsync(jobId, CancellationToken.None)).Outcome.Should().Be(LookupOutcome.NotFound);
        (await Respond(HttpStatusCode.InternalServerError).GetAsync(jobId, CancellationToken.None)).Outcome.Should().Be(LookupOutcome.Unreachable);
        (await Client(_ => throw new HttpRequestException()).GetAsync(jobId, CancellationToken.None)).Outcome.Should().Be(LookupOutcome.Unreachable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Conflict, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Cancel_treats_already_gone_as_success(HttpStatusCode status, bool expected) =>
        (await Respond(status).CancelAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(expected);

    [Fact]
    public async Task Cancel_never_throws() =>
        (await Client(_ => throw new HttpRequestException()).CancelAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
}
