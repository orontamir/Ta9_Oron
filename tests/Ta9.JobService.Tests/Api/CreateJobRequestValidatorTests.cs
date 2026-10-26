using Ta9.Contracts;
using Ta9.JobService.Api;

namespace Ta9.JobService.Tests.Api;

public class CreateJobRequestValidatorTests
{
    private static CreateJobRequest Valid(int documents = 2) =>
        new("job", Enumerable.Range(0, documents).Select(i => new CreateJobDocumentRequest($"d{i}", "text")).ToList());

    [Fact]
    public void Accepts_valid_request() =>
        CreateJobRequestValidator.Validate(Valid()).Should().BeEmpty();

    [Fact]
    public void Accepts_the_maximum_number_of_documents() =>
        CreateJobRequestValidator.Validate(Valid(JobLimits.MaxDocumentsPerJob)).Should().BeEmpty();

    [Fact]
    public void Rejects_too_many_documents_with_a_single_error()
    {
        var errors = CreateJobRequestValidator.Validate(Valid(JobLimits.MaxDocumentsPerJob + 1));

        errors.Should().ContainSingle().Which.Key.Should().Be("documents");
    }

    [Fact]
    public void Rejects_too_long_job_name() =>
        CreateJobRequestValidator.Validate(Valid() with { Name = new string('n', JobLimits.MaxJobNameLength + 1) })
            .Should().ContainKey("name");

    [Fact]
    public void Job_name_is_optional() =>
        CreateJobRequestValidator.Validate(Valid() with { Name = null }).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Document_name_is_required(string? name)
    {
        var request = Valid() with { Documents = [new CreateJobDocumentRequest(name!, "text")] };

        CreateJobRequestValidator.Validate(request).Should().ContainKey("documents[0].name");
    }

    [Fact]
    public void Rejects_null_request() =>
        CreateJobRequestValidator.Validate(null).Should().ContainKey("body");
}
