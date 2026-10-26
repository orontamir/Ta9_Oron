using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class ProgressThrottleTests
{
    [Fact]
    public void Reports_every_ten_percent_but_not_at_completion()
    {
        var throttle = new ProgressThrottle(1000, new ManualTimeProvider());

        var reportedAt = Enumerable.Range(1, 1000).Where(throttle.ShouldReport).ToList();

        reportedAt.Should().Equal(100, 200, 300, 400, 500, 600, 700, 800, 900);
    }

    [Fact]
    public void Reports_when_the_interval_elapses_between_steps()
    {
        var time = new ManualTimeProvider();
        var throttle = new ProgressThrottle(1000, time);

        throttle.ShouldReport(5).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(2));
        throttle.ShouldReport(6).Should().BeTrue();
        throttle.ShouldReport(7).Should().BeFalse("the timer was reset by the last report");
    }

    [Fact]
    public void Small_jobs_report_each_document_except_the_last()
    {
        var throttle = new ProgressThrottle(3, new ManualTimeProvider());

        Enumerable.Range(1, 3).Where(throttle.ShouldReport).Should().Equal(1, 2);
    }

    [Fact]
    public void Out_of_order_counts_do_not_produce_duplicate_reports()
    {
        var throttle = new ProgressThrottle(100, new ManualTimeProvider());

        throttle.ShouldReport(12).Should().BeTrue();
        throttle.ShouldReport(10).Should().BeFalse();
        throttle.ShouldReport(15).Should().BeFalse();
        throttle.ShouldReport(20).Should().BeTrue();
    }
}
