using System.Diagnostics;
using Ta9.ServiceDefaults.Logging;
using static Ta9.ServiceDefaults.Tests.Logging.LogTestData;

namespace Ta9.ServiceDefaults.Tests.Logging;

public class BatchingLogSinkTests
{
    [Fact]
    public void Writes_events_in_order_in_batches_no_larger_than_the_batch_size()
    {
        var writer = new FakeLogWriter();
        var sink = new BatchingLogSink(writer, "svc", Options(batchSize: 10, flushInterval: TimeSpan.FromMinutes(1)));

        for (var i = 0; i < 25; i++)
        {
            sink.Emit(Event($"m{i}"));
        }

        sink.Dispose();

        writer.Batches.Should().OnlyContain(b => b.Count <= 10);
        writer.Records.Select(r => r.Message).Should().Equal(Enumerable.Range(0, 25).Select(i => $"m{i}"));
    }

    [Fact]
    public async Task Flushes_a_partial_batch_after_the_flush_interval()
    {
        var writer = new FakeLogWriter();
        using var sink = new BatchingLogSink(writer, "svc", Options(batchSize: 100, flushInterval: TimeSpan.FromMilliseconds(50)));

        sink.Emit(Event("one"));
        sink.Emit(Event("two"));

        await EventuallyAsync(() => writer.Records.Count == 2, "a partial batch must be written without waiting for it to fill");
    }

    [Fact]
    public void Dispose_writes_pending_events_before_returning()
    {
        var writer = new FakeLogWriter();
        var sink = new BatchingLogSink(writer, "svc", Options(flushInterval: TimeSpan.FromHours(1)));
        for (var i = 0; i < 5; i++)
        {
            sink.Emit(Event($"m{i}"));
        }

        sink.Dispose();

        writer.Records.Should().HaveCount(5);
    }

    [Fact]
    public async Task Full_buffer_drops_events_without_blocking_and_reports_the_count()
    {
        var gate = new TaskCompletionSource();
        var writing = new TaskCompletionSource();
        var writer = new FakeLogWriter
        {
            OnWrite = async attempt =>
            {
                if (attempt == 1)
                {
                    writing.TrySetResult();
                    await gate.Task;
                }
            }
        };
        var sink = new BatchingLogSink(writer, "svc", Options(queueCapacity: 5));
        sink.Emit(Event("first"));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the writer is now stuck (slow database)

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            sink.Emit(Event($"m{i}"));
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "logging must never block the caller");
        sink.DroppedCount.Should().Be(15, "the buffer holds 5 events");

        gate.SetResult();
        sink.Dispose();

        writer.Records.Select(r => r.Message).Should().Contain(["first", "m0", "m4"]).And.NotContain("m5");
        var notice = writer.Records.Should().ContainSingle(r => r.Level == "Warning" && r.Properties.ContainsKey("DroppedCount")).Which;
        notice.Properties["DroppedCount"].Should().Be("15");
        notice.Message.Should().Contain("15 log events were not stored");
    }

    [Fact]
    public async Task Failed_write_is_reported_and_later_events_still_reach_the_store()
    {
        var diagnostics = new StringWriter();
        var writer = new FakeLogWriter
        {
            OnWrite = attempt => attempt == 1 ? throw new TimeoutException("log database unreachable") : Task.CompletedTask
        };
        var sink = new BatchingLogSink(writer, "svc", Options(), TextWriter.Synchronized(diagnostics));

        sink.Emit(Event("lost"));
        await EventuallyAsync(() => writer.Attempts >= 1, "the first write must be attempted");
        sink.Emit(Event("kept"));
        sink.Dispose();

        writer.Records.Select(r => r.Message).Should().Contain("kept").And.NotContain("lost");
        writer.Records.Should().ContainSingle(r => r.Level == "Warning" && r.Properties.GetValueOrDefault("DroppedCount") == "1");
        diagnostics.ToString().Should().Contain(BatchingLogSink.DiagnosticPrefix).And.Contain("could not store 1 log events")
            .And.Contain("log database unreachable");
    }

    [Fact]
    public void Emit_after_dispose_is_counted_as_dropped_and_does_not_throw()
    {
        var sink = new BatchingLogSink(new FakeLogWriter(), "svc", Options());
        sink.Dispose();

        var emit = () => sink.Emit(Event("late"));

        emit.Should().NotThrow();
        sink.DroppedCount.Should().Be(1);
    }
}
