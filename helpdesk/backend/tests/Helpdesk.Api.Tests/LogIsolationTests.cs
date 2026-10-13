namespace Helpdesk.Api.Tests;

public class LogIsolationTests
{
    /// <summary>
    /// Request logging used to write to Serilog's process-wide logger, which belongs to whichever host was built
    /// last, so one host's request lines appeared in another's output. That made "this is (not) in the logs" tests
    /// unreliable when hosts ran in parallel.
    /// </summary>
    [Fact]
    public async Task EachHost_KeepsItsOwnRequestLogs()
    {
        using var first = new ApiFactory();
        using var second = new ApiFactory();
        var clientFirst = first.CreateClient();
        second.CreateClient(); // built last, so it would own a shared static logger

        await clientFirst.GetAsync("/healthz");

        Assert.Contains(first.Logs.Lines, l => l.Contains("/healthz"));
        Assert.DoesNotContain(second.Logs.Lines, l => l.Contains("/healthz"));
    }
}
