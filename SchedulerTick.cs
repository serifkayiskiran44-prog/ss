namespace TrMarketplaceHubDesktop;

/// The exact per-tick gate MainWindow.ScheduledAsync uses to decide whether to do any
/// work at all. Extracted so the XML-due / automation-due independence it encodes -
/// zero due XML sources must never skip due stock/price/health/sync automation jobs,
/// and vice versa - is unit-testable without a WPF host.
public static class SchedulerTick
{
    public static bool ShouldRun<TXml, TAutomation>(IReadOnlyCollection<TXml> dueXmlSources, IReadOnlyCollection<TAutomation> dueAutomationJobs)
        => dueXmlSources.Count > 0 || dueAutomationJobs.Count > 0;
}
