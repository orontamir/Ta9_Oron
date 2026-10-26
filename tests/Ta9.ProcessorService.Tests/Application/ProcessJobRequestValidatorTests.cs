using Ta9.Contracts;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class ProcessJobRequestValidatorTests
{
    private static ProcessJobRequest Valid() => new(Guid.NewGuid(), TestData.Documents("hello", "world"));

    [Fact]
    public void Accepts_valid_request() =>
        ProcessJobRequestValidator.Validate(Valid()).Should().BeEmpty();

    [Fact]
    public void Rejects_null_request() =>
        ProcessJobRequestValidator.Validate(null).Should().ContainKey("body");

    [Fact]
    public void Rejects_empty_job_id() =>
        ProcessJobRequestValidator.Validate(Valid() with { JobId = Guid.Empty }).Should().ContainKey("jobId");

    [Fact]
    public void Rejects_empty_documents() =>
        ProcessJobRequestValidator.Validate(Valid() with { Documents = [] }).Should().ContainKey("documents");

    [Fact]
    public void Rejects_too_many_documents() =>
        ProcessJobRequestValidator.Validate(Valid() with { Documents = TestData.Documents(JobLimits.MaxDocumentsPerJob + 1) })
            .Should().ContainKey("documents");

    [Fact]
    public void Accepts_exactly_the_maximum_number_of_documents() =>
        ProcessJobRequestValidator.Validate(Valid() with { Documents = TestData.Documents(JobLimits.MaxDocumentsPerJob) })
            .Should().BeEmpty();

    [Fact]
    public void Rejects_duplicate_and_negative_indexes()
    {
        var request = Valid() with
        {
            Documents =
            [
                new ProcessDocumentRequest(0, "a", "x"),
                new ProcessDocumentRequest(0, "b", "y"),
                new ProcessDocumentRequest(-1, "c", "z")
            ]
        };

        var errors = ProcessJobRequestValidator.Validate(request);

        errors.Should().ContainKey("documents[1].index");
        errors.Should().ContainKey("documents[2].index");
    }

    [Fact]
    public void Rejects_null_content()
    {
        var request = Valid() with { Documents = [new ProcessDocumentRequest(0, "a", null!)] };

        ProcessJobRequestValidator.Validate(request).Should().ContainKey("documents[0].content");
    }

    [Fact]
    public void Measures_content_size_in_utf8_bytes()
    {
        // Hebrew letters are 2 bytes in UTF-8: half the limit in characters is already too large.
        var hebrew = new string('ש', JobLimits.MaxDocumentBytes / 2 + 1);
        var request = Valid() with { Documents = [new ProcessDocumentRequest(0, "a", hebrew)] };

        ProcessJobRequestValidator.Validate(request).Should().ContainKey("documents[0].content");
    }

    [Fact]
    public void Accepts_content_at_the_size_limit()
    {
        var request = Valid() with { Documents = [new ProcessDocumentRequest(0, "a", new string('a', JobLimits.MaxDocumentBytes))] };

        ProcessJobRequestValidator.Validate(request).Should().BeEmpty();
    }

    [Fact]
    public void Rejects_too_long_name()
    {
        var request = Valid() with { Documents = [new ProcessDocumentRequest(0, new string('n', JobLimits.MaxDocumentNameLength + 1), "x")] };

        ProcessJobRequestValidator.Validate(request).Should().ContainKey("documents[0].name");
    }

    [Fact]
    public void Accepts_empty_content() =>
        ProcessJobRequestValidator.Validate(Valid() with { Documents = TestData.Documents("") }).Should().BeEmpty();
}
