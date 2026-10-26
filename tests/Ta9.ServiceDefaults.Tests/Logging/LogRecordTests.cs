using MongoDB.Bson;
using Serilog.Events;
using Ta9.ServiceDefaults.Logging;
using static Ta9.ServiceDefaults.Tests.Logging.LogTestData;

namespace Ta9.ServiceDefaults.Tests.Logging;

public class LogRecordTests
{
    [Fact]
    public void Maps_level_rendered_message_service_host_and_exception()
    {
        var error = new InvalidOperationException("boom");

        var record = LogRecord.From(Event("Job {JobName} failed", LogEventLevel.Error, error, ("JobName", "nightly")), "job-service", "host-1");

        record.Level.Should().Be("Error");
        record.Message.Should().Be("Job nightly failed");
        record.MessageTemplate.Should().Be("Job {JobName} failed");
        record.Service.Should().Be("job-service");
        record.Host.Should().Be("host-1");
        record.Exception.Should().Contain("InvalidOperationException").And.Contain("boom");
        record.Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Renders_string_values_without_quotes_and_other_values_as_serilog_does()
    {
        var record = LogRecord.From(Event("Title {Title} took {Ms} ms", properties: [("Title", "Storage unavailable"), ("Ms", 12)]), "svc", "host");

        record.Message.Should().Be("Title Storage unavailable took 12 ms");
    }

    [Fact]
    public void Promotes_correlation_and_job_ids_and_keeps_other_properties_as_text()
    {
        var jobId = Guid.NewGuid();

        var record = LogRecord.From(
            Event("Processed {Count}", properties: [("CorrelationId", "corr-1"), ("JobId", jobId), ("SourceContext", "Ta9.X"), ("Count", 5)]),
            "svc", "host");

        record.CorrelationId.Should().Be("corr-1");
        record.JobId.Should().Be(jobId.ToString());
        record.SourceContext.Should().Be("Ta9.X");
        record.Message.Should().Be("Processed 5");
        record.Properties.Should().BeEquivalentTo(new Dictionary<string, string> { ["Count"] = "5" });
    }

    [Fact]
    public void Truncates_oversized_message_and_exception()
    {
        var huge = new string('x', LogRecord.MaxMessageLength * 2);

        var record = LogRecord.From(Event("{Text}", exception: new Exception(huge), properties: [("Text", huge)]), "svc", "host");

        record.Message.Length.Should().BeLessThan(LogRecord.MaxMessageLength + 20);
        record.Message.Should().EndWith("[truncated]");
        record.Exception!.Length.Should().BeLessThan(LogRecord.MaxExceptionLength + 20);
        record.Properties["Text"].Length.Should().BeLessThan(LogRecord.MaxPropertyLength + 20);
    }

    [Fact]
    public void Bson_document_has_indexed_fields_and_safe_property_names()
    {
        var jobId = Guid.NewGuid();
        var record = LogRecord.From(
            Event("x", properties: [("JobId", jobId), ("CorrelationId", "c"), ("a.b", 1), ("$where", 2)]), "svc", "host");

        var document = MongoLogBatchWriter.ToBson(record);

        document["timestamp"].BsonType.Should().Be(BsonType.DateTime, "the TTL index needs a date");
        document["jobId"].AsString.Should().Be(jobId.ToString());
        document["correlationId"].AsString.Should().Be("c");
        document["service"].AsString.Should().Be("svc");
        document.Contains("exception").Should().BeFalse("absent values are omitted");
        document["properties"].AsBsonDocument.Names.Should().BeEquivalentTo("a_b", "_where");
    }
}
