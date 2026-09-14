using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Regression coverage for issue #307: MainWindow.ScheduledAsync's old
/// `if(due.Count==0)return;` used only the XML-due count, so zero due XML sources
/// silently skipped due stock/price/health/sync automation jobs on that tick.
/// SchedulerTick.ShouldRun is the exact gate the production tick now calls.
[TestClass]
public sealed class SchedulerTickTests
{
    [TestMethod]
    public void NoDueXmlButDueAutomationStillRuns()
    {
        Assert.IsTrue(SchedulerTick.ShouldRun(Array.Empty<XmlSource>(), new[] { new AutomationJob() }));
    }

    [TestMethod]
    public void DueXmlButNoDueAutomationStillRuns()
    {
        Assert.IsTrue(SchedulerTick.ShouldRun(new[] { new XmlSource() }, Array.Empty<AutomationJob>()));
    }

    [TestMethod]
    public void NeitherDueSkipsTheTick()
    {
        Assert.IsFalse(SchedulerTick.ShouldRun(Array.Empty<XmlSource>(), Array.Empty<AutomationJob>()));
    }

    [TestMethod]
    public void DisabledXmlSourceWithDueStockJobIsEvaluatedAsRunnable()
    {
        var root = Path.Combine(Path.GetTempPath(), "scheduler-tick-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        var automation = new AutomationStore(root);
        try
        {
            catalog.SaveSource(new XmlSource { Id = Guid.NewGuid().ToString("N"), Enabled = false, AutoImport = false, Location = "https://example.test/feed.xml" });
            automation.Save(new AutomationJob { Kind = AutomationKind.Stock, Channel = "etsy", Shop = "default", NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Enabled = true });

            var dueXml = catalog.Sources().Where(s => s.Enabled && s.AutoImport && DateTime.UtcNow - (s.LastRunUtc ?? DateTime.MinValue) >= TimeSpan.FromMinutes(s.IntervalMinutes)).ToList();
            var dueAutomation = automation.List().Where(j => j.Enabled && j.NextRunUtc <= DateTime.UtcNow).ToList();

            Assert.AreEqual(0, dueXml.Count);
            Assert.AreEqual(1, dueAutomation.Count);
            Assert.IsTrue(SchedulerTick.ShouldRun(dueXml, dueAutomation), "A due automation job must not be skipped just because no XML source is due.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
