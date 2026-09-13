using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #898 (SOURCE SLA: per-source refresh SLA profile). A source promises a successful feed every N minutes with a
// grace; it is on time, overdue, never-delivered or out of scope while disabled — judged in UTC from recorded facts,
// so neither a wall-clock change nor a restart can move the verdict. The scheduler retries an overdue source at the
// grace cadence, a crossing is recorded once, and the state reaches the source list, the health panel and the
// dashboard's anomaly cards.
[TestClass]
public sealed class SourceSlaTests
{
    // The night European clocks jump forward (01:00 UTC on the last Sunday of March).
    static readonly DateTime Now = new(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);
    static XmlSource Source(int interval = 30, int refresh = 0, int grace = 0, bool enabled = true, DateTime? fed = null) => new()
    {
        Id = "s", Name = "Tedarikçi S", Location = "https://feeds.example.com/s.xml?key=abc123", Enabled = enabled, AutoImport = true, IntervalMinutes = interval,
        SlaRefreshMinutes = refresh, SlaGraceMinutes = grace, LastSuccessfulFeedUtc = fed, ItemPath = "/p",
    };

    [TestMethod]
    public void TheProfileDefaultsToTheCheckIntervalAndJudgesOnTimeOverdueNeverAndDisabledInUtc()
    {
        // Defaults: refresh = check interval; grace = half of it, at least fifteen minutes.
        Assert.AreEqual(TimeSpan.FromMinutes(30), SourceSla.Refresh(Source(30))); Assert.AreEqual(TimeSpan.FromMinutes(15), SourceSla.Grace(Source(30)));
        Assert.AreEqual(TimeSpan.FromMinutes(120), SourceSla.Refresh(Source(30, refresh: 120))); Assert.AreEqual(TimeSpan.FromMinutes(60), SourceSla.Grace(Source(30, refresh: 120)));
        Assert.AreEqual(TimeSpan.FromMinutes(10), SourceSla.Grace(Source(30, grace: 10)));

        // On time: fed twenty minutes ago against 30 + 15.
        var onTime = SourceSla.Evaluate(Source(fed: Now.AddMinutes(-20)), Now);
        Assert.AreEqual(SourceSlaState.OnTime, onTime.State); Assert.AreEqual("zamanında", onTime.Word); StringAssert.Contains(onTime.Detail, "son tarihe 25 dk"); StringAssert.Contains(onTime.Detail, "beklenen her 30 dk, tolerans 15 dk");
        Assert.AreEqual(Now.AddMinutes(25), onTime.DeadlineUtc); Assert.AreEqual(SeverityLevel.Success, onTime.Level);

        // Overdue: fed three hours ago -> two hours and a quarter late.
        var overdue = SourceSla.Evaluate(Source(fed: Now.AddHours(-3)), Now);
        Assert.AreEqual(SourceSlaState.Overdue, overdue.State); Assert.AreEqual("gecikmiş", overdue.Word); Assert.AreEqual(TimeSpan.FromMinutes(135), overdue.OverdueBy);
        StringAssert.Contains(overdue.Detail, "2 sa 15 dk gecikti"); Assert.AreEqual(SeverityLevel.Warning, overdue.Level); StringAssert.Contains(overdue.Line, "gecikmiş · ");

        // Exactly at the deadline is still on time; one second later is overdue.
        Assert.AreEqual(SourceSlaState.OnTime, SourceSla.Evaluate(Source(fed: Now.AddMinutes(-45)), Now).State);
        Assert.AreEqual(SourceSlaState.Overdue, SourceSla.Evaluate(Source(fed: Now.AddMinutes(-45).AddSeconds(-1)), Now).State);

        // Never delivered; disabled even when it would be overdue.
        var never = SourceSla.Evaluate(Source(), Now); Assert.AreEqual(SourceSlaState.NeverDelivered, never.State); Assert.AreEqual("hiç teslim etmedi", never.Word);
        var disabled = SourceSla.Evaluate(Source(enabled: false, fed: Now.AddDays(-3)), Now); Assert.AreEqual(SourceSlaState.Disabled, disabled.State); Assert.AreEqual("pasif", disabled.Word); Assert.AreEqual(SeverityLevel.Info, disabled.Level);

        // UTC only: the same instant given as local wall-clock time, or with an unspecified kind, gives the same verdict; and the
        // verdict depends on the UTC distance alone, so the same distance two hundred days later (across any DST change) reads the same.
        Assert.AreEqual(overdue, SourceSla.Evaluate(Source(fed: Now.AddHours(-3)), Now.ToLocalTime()));
        Assert.AreEqual(overdue, SourceSla.Evaluate(Source(fed: Now.AddHours(-3)), DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        var shifted = SourceSla.Evaluate(Source(fed: Now.AddDays(200).AddHours(-3)), Now.AddDays(200));
        Assert.AreEqual(overdue.Detail, shifted.Detail); Assert.AreEqual(overdue.OverdueBy, shifted.OverdueBy);

        // The scheduler's retry rule: an overdue auto-import source is retried once a grace has passed since its last attempt.
        var retry = Source(fed: Now.AddHours(-3)); retry.LastRunUtc = Now.AddMinutes(-16); Assert.IsTrue(SourceSla.DueForRetry(retry, Now));
        retry.LastRunUtc = Now.AddMinutes(-5); Assert.IsFalse(SourceSla.DueForRetry(retry, Now), "not before the grace has passed since the last attempt");
        Assert.IsFalse(SourceSla.DueForRetry(Source(fed: Now.AddMinutes(-20)), Now), "an on-time source keeps its check interval");
        var noAuto = Source(fed: Now.AddHours(-3)); noAuto.AutoImport = false; Assert.IsFalse(SourceSla.DueForRetry(noAuto, Now));

        // A crossing is recorded once: a first on-time state silently, a first overdue one reported, every later change reported.
        var s = Source(fed: Now.AddMinutes(-20));
        Assert.IsFalse(SourceSla.Transition(s, SourceSla.Evaluate(s, Now))); Assert.AreEqual("OnTime", s.LastSlaState);
        Assert.IsTrue(SourceSla.Transition(s, SourceSla.Evaluate(s, Now.AddHours(3)))); Assert.AreEqual("Overdue", s.LastSlaState);
        Assert.IsFalse(SourceSla.Transition(s, SourceSla.Evaluate(s, Now.AddHours(4))), "still overdue: no second report");
        s.LastSuccessfulFeedUtc = Now.AddHours(4);
        Assert.IsTrue(SourceSla.Transition(s, SourceSla.Evaluate(s, Now.AddHours(4).AddMinutes(1)))); Assert.AreEqual("OnTime", s.LastSlaState);
        var fresh = Source(fed: Now.AddHours(-3)); Assert.IsTrue(SourceSla.Transition(fresh, SourceSla.Evaluate(fresh, Now)), "a first overdue is reported");

        // The audit row carries the name and the verdict, never the address.
        var audit = SourceSla.ToAudit(s, SourceSla.Evaluate(s, Now.AddHours(5)));
        Assert.AreEqual("import", audit.Module); Assert.AreEqual(SourceSla.AuditAction, audit.Action); Assert.AreEqual("Warning", audit.Outcome);
        StringAssert.Contains(audit.Detail, "Tedarikçi S"); StringAssert.Contains(audit.Detail, "gecikmiş"); Assert.IsFalse(audit.Detail.Contains("example.com") || audit.Detail.Contains("abc123"), audit.Detail);
    }

    [TestMethod]
    public void TheProfileIsRevisionedSurvivesARestartAndReachesTheListTheHealthPanelAndTheDashboardCards()
    {
        var root = Path.Combine(Path.GetTempPath(), "sla-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root);
            var s = Source(fed: Now.AddHours(-3)); s.SlaRefreshMinutes = 60; s.SlaGraceMinutes = 20; store.SaveSource(s);
            var revision = s.ConfigRevision;
            var loaded = store.Sources().Single();
            Assert.AreEqual(60, loaded.SlaRefreshMinutes); Assert.AreEqual(20, loaded.SlaGraceMinutes);

            // The profile is configuration: changing it is a new revision, and the revision snapshot carries it.
            loaded.SlaGraceMinutes = 30; store.SaveSource(loaded);
            Assert.AreEqual(revision + 1, loaded.ConfigRevision); Assert.AreEqual(30, store.SourceRevision(loaded.Id, loaded.ConfigRevision)!.Config.SlaGraceMinutes);

            // The recorded state is not configuration (no revision) and survives a restart: the same word, no second crossing.
            var verdict = SourceSla.Evaluate(loaded, Now);
            Assert.AreEqual(SourceSlaState.Overdue, verdict.State); Assert.IsTrue(SourceSla.Transition(loaded, verdict)); store.SaveSource(loaded);
            Assert.AreEqual(revision + 1, loaded.ConfigRevision, "a state word is not a configuration change");
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root); var again = reopened.Sources().Single();
            Assert.AreEqual("Overdue", again.LastSlaState); Assert.IsFalse(SourceSla.Transition(again, SourceSla.Evaluate(again, Now.AddMinutes(1))));

            // The source list band names the SLA; the health panel gets a line and a degraded verdict; the dashboard gets a card.
            var row = new ImportSourceRow(again.Id, again.Name, ImportSourceKind.XmlUrl, "XML", true, true, false, "", "", "");
            var (band, problem) = XmlSourceListGrouping.Classify(again, row, null, Now);
            Assert.AreEqual(SourceHealthBand.Problem, band); StringAssert.Contains(problem, "güncellik SLA"); StringAssert.Contains(problem, "gecikmiş");
            var facts = new SourceHealthFacts("HEALTHY", Now.AddMinutes(-5), 250, 200, "", Now.AddHours(-3), 120, "COMPLETE", 2, 2, "shape-a", "shape-a", null, 120, 0, 0, null, null, null, null, null, false) { Sla = verdict };
            var panel = XmlSourceHealthPanel.Compose(facts, Now);
            Assert.AreEqual(9, panel.Lines.Count); Assert.AreEqual("Güncellik SLA", panel.Lines[8].Label); Assert.AreEqual("gecikmiş", panel.Lines[8].Value); Assert.AreEqual(SeverityLevel.Warning, panel.Lines[8].Level);
            Assert.AreEqual(SourceHealthVerdict.Degraded, panel.Verdict);
            var onTimePanel = XmlSourceHealthPanel.Compose(facts with { Sla = SourceSla.Evaluate(Source(fed: Now.AddMinutes(-10)), Now) }, Now);
            Assert.AreEqual("zamanında", onTimePanel.Lines[8].Value); Assert.AreEqual(SourceHealthVerdict.Healthy, onTimePanel.Verdict);
            var cards = DashboardAnomalies.Project(new DashboardAnomalyInput { Scope = "tüm mağazalar", OverdueSources = 2, OverdueSourceOldestUtc = Now.AddHours(-3) }, Now).Cards;
            var card = cards.Single(c => c.Key == "sla-overdue");
            Assert.AreEqual(2, card.Count); Assert.AreEqual("xml", card.Route); Assert.AreEqual(DashboardAnomalies.Warning, card.Severity); StringAssert.Contains(card.Title, "SLA");
            Assert.IsFalse(DashboardAnomalies.Project(new DashboardAnomalyInput { Scope = "tüm mağazalar" }, Now).Cards.Any(c => c.Key == "sla-overdue"));
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
