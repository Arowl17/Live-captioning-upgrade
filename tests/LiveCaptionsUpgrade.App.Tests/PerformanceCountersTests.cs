using System;
using Xunit;

namespace LiveCaptionsUpgrade.AppTests;

public class PerformanceCountersTests
{
    [Fact]
    public void Report_says_what_was_counted_and_starts_afresh()
    {
        var counters = new global::LiveCaptionsUpgrade.PerformanceCounters();
        counters.CountRead(TimeSpan.FromMilliseconds(1), 300);
        counters.CountRead(TimeSpan.FromMilliseconds(3), 450);
        counters.Changes++;

        // This process stands in for Live Captions; the first report has nothing to compare its time with.
        string first = counters.Report(Environment.ProcessId);
        Assert.Contains("2 reads of the captions (2.00 ms each, slowest 3 ms, up to 450 characters), 1 with new text", first, StringComparison.Ordinal);
        Assert.Contains("Live Captions ?", first, StringComparison.Ordinal);
        Assert.True(counters.Logged);

        string second = counters.Report(Environment.ProcessId);
        Assert.Contains("0 reads of the captions", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Live Captions ?", second, StringComparison.Ordinal);

        // No Live Captions (it closed): still a report.
        Assert.Contains("Live Captions ?", counters.Report(0), StringComparison.Ordinal);
    }
}
