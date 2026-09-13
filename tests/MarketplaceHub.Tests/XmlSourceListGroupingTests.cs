using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #829 (DESIGN: XML source list grouping and health scan). One band per source from recorded state only; last
// success, next schedule and active run per entry; band and text filters over masked text; 100 mixed sources
// grouped in order with counts that add up; secrets in addresses or errors never reach the list.
[TestClass]
public sealed class XmlSourceListGroupingTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static bool NoFile(string _) => false;
    static ImportSourceRow Row(XmlSource s, bool recent = false) => ImportSourceCatalog.Describe(s, null, recent, Now, NoFile);
    static SourceListEntry Entry(XmlSource s, SourceRunSnapshot run = null, bool recent = false) => XmlSourceListGrouping.Describe(s, Row(s, recent), run, Now);

    [TestMethod]
    public void EverySourceLandsInExactlyOneBandFromWhatIsRecorded()
    {
        var running = Entry(new XmlSource { Name = "Run", Location = "https://r.example.com/f.xml" }, new("Running", Now.AddMinutes(-3), null, Now.AddMinutes(7), ""));
        Assert.AreEqual(SourceHealthBand.Running, running.Band); StringAssert.Contains(running.ActiveRun, "sürüyor · 3 dk");

        var stale = Entry(new XmlSource { Name = "Stale", Location = "https://s.example.com/f.xml" }, new("Running", Now.AddHours(-2), null, Now.AddMinutes(-50), ""));
        Assert.AreEqual(SourceHealthBand.Problem, stale.Band, "A running record whose lease expired is not running -- it is stuck."); StringAssert.Contains(stale.Problem, "askıda");

        var timeout = Entry(new XmlSource { Name = "Down", Location = "https://d.example.com/f.xml", LastHealthState = "TIMEOUT", LastHealthError = "XML kaynağı zaman aşımına uğradı." });
        Assert.AreEqual(SourceHealthBand.Problem, timeout.Band); StringAssert.Contains(timeout.Problem, "zaman aşımı");

        var slow = Entry(new XmlSource { Name = "Slow", Location = "https://sl.example.com/f.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 8200 });
        Assert.AreEqual(SourceHealthBand.Problem, slow.Band); StringAssert.Contains(slow.Problem, "yavaş");

        var failedRun = Entry(new XmlSource { Name = "Failed", Location = "https://fr.example.com/f.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 120 }, new("Failed", Now.AddHours(-1), Now.AddHours(-1), null, "Zorunlu XML alanları bulunamadı: Sku"));
        Assert.AreEqual(SourceHealthBand.Problem, failedRun.Band); StringAssert.Contains(failedRun.Problem, "başarısız"); StringAssert.Contains(failedRun.Problem, "Sku");

        var incomplete = Entry(new XmlSource { Name = "Inc", Location = "https://i.example.com/f.xml", LastHealthState = "HEALTHY", LastFeedState = "INCOMPLETE", LastHealthLatencyMs = 90 });
        Assert.AreEqual(SourceHealthBand.Problem, incomplete.Band); StringAssert.Contains(incomplete.Problem, "eksik akış");

        var healthy = Entry(new XmlSource { Name = "Ok", Location = "https://ok.example.com/f.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 300, LastFeedState = "COMPLETE", LastSuccessfulFeedUtc = Now.AddHours(-2), AutoImport = true, LastRunUtc = Now.AddMinutes(-10), IntervalMinutes = 30, SlaRefreshMinutes = 120, SlaGraceMinutes = 30 }, new("Completed", Now.AddMinutes(-10), Now.AddMinutes(-9), null, ""));
        Assert.AreEqual(SourceHealthBand.Healthy, healthy.Band);
        Assert.AreEqual("2 sa önce", healthy.LastSuccess); Assert.AreEqual("20 dk sonra", healthy.NextSchedule); Assert.AreEqual("", healthy.ActiveRun);
        StringAssert.Contains(healthy.Detail, "son başarı 2 sa önce"); StringAssert.Contains(healthy.Detail, "sonraki: 20 dk sonra");
        // #898: the same source on the default profile (30 + 15 minutes) has missed its promised refresh -- a problem even though it is reachable.
        var overdue = Entry(new XmlSource { Name = "Late", Location = "https://late.example.com/f.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 300, LastFeedState = "COMPLETE", LastSuccessfulFeedUtc = Now.AddHours(-2), AutoImport = true, LastRunUtc = Now.AddMinutes(-10), IntervalMinutes = 30 });
        Assert.AreEqual(SourceHealthBand.Problem, overdue.Band); StringAssert.Contains(overdue.Problem, "güncellik SLA");

        var never = Entry(new XmlSource { Name = "New", Location = "https://n.example.com/f.xml" });
        Assert.AreEqual(SourceHealthBand.NeverRun, never.Band); Assert.AreEqual("hiç", never.LastSuccess); Assert.AreEqual("otomatik değil", never.NextSchedule);

        var disabled = Entry(new XmlSource { Name = "Off", Location = "https://o.example.com/f.xml", Enabled = false, LastHealthState = "TIMEOUT" });
        Assert.AreEqual(SourceHealthBand.Disabled, disabled.Band, "Disabled wins over its old health."); Assert.AreEqual("pasif", disabled.NextSchedule);

        var unsupported = Entry(new XmlSource { Name = "Old", Location = "http://u.example.com/f.xml" });
        Assert.AreEqual(SourceHealthBand.Unsupported, unsupported.Band);

        var due = Entry(new XmlSource { Name = "Due", Location = "https://du.example.com/f.xml", AutoImport = true, LastRunUtc = Now.AddHours(-3), IntervalMinutes = 30 });
        Assert.AreEqual("sıradaki kontrolde", due.NextSchedule);
    }

    [TestMethod]
    public void AHundredMixedSourcesGroupInBandOrderWithCountsThatAddUp()
    {
        var entries = new List<SourceListEntry>();
        for (var i = 0; i < 100; i++)
        {
            var s = new XmlSource { Name = $"Kaynak {i:D3}", Location = i % 10 == 9 ? "http://x.example.com/f.xml" : $"https://h{i}.example.com/f.xml", Enabled = i % 10 != 8 };
            if (i % 10 is 0 or 1) { s.LastHealthState = "HEALTHY"; s.LastHealthLatencyMs = 100; s.LastRunUtc = Now.AddMinutes(-5); }
            if (i % 10 is 2) s.LastHealthState = "TIMEOUT";
            if (i % 10 is 3) { s.LastHealthState = "HEALTHY"; s.LastHealthLatencyMs = 9000; }
            SourceRunSnapshot run = i % 10 == 4 ? new("Running", Now.AddSeconds(-30), null, Now.AddMinutes(9), "") : null;
            entries.Add(Entry(s, run, recent: i == 1));
        }

        var counts = XmlSourceListGrouping.Counts(entries);
        Assert.AreEqual(100, counts.Values.Sum());
        Assert.AreEqual(10, counts[SourceHealthBand.Running]); Assert.AreEqual(20, counts[SourceHealthBand.Problem]); Assert.AreEqual(20, counts[SourceHealthBand.Healthy]);
        Assert.AreEqual(30, counts[SourceHealthBand.NeverRun]); Assert.AreEqual(10, counts[SourceHealthBand.Disabled]); Assert.AreEqual(10, counts[SourceHealthBand.Unsupported]);

        var groups = XmlSourceListGrouping.Group(entries, new());
        CollectionAssert.AreEqual(XmlSourceListGrouping.Bands.Select(b => b.Band).ToArray(), groups.Select(g => g.Band).ToArray(), "Every band present, in order.");
        Assert.AreEqual(100, groups.Sum(g => g.Entries.Count));
        Assert.AreEqual("Kaynak 001", groups.Single(g => g.Band == SourceHealthBand.Healthy).Entries[0].Row.Title, "The last-used leads its band.");
        Assert.AreEqual("Kaynak 000", groups.Single(g => g.Band == SourceHealthBand.Healthy).Entries[1].Row.Title);

        var problems = XmlSourceListGrouping.Group(entries, new(SourceHealthBand.Problem));
        Assert.AreEqual(1, problems.Count); Assert.AreEqual(20, problems[0].Entries.Count);
        var byText = XmlSourceListGrouping.Group(entries, new(null, "h42.example"));
        Assert.AreEqual(1, byText.Sum(g => g.Entries.Count), "Text matches the masked address.");
        Assert.AreEqual(0, XmlSourceListGrouping.Group(entries, new(SourceHealthBand.Disabled, "Kaynak 000")).Count, "Both filters apply.");
    }

    [TestMethod]
    public void SecretsInAddressesAndErrorsNeverReachTheListOrItsSearch()
    {
        var s = new XmlSource { Name = "Gizli", Location = "https://ali:parola@g.example.com/f.xml?token=SECRET999&v=2", LastHealthState = "AUTH_ERROR", LastHealthError = "401 for token=SECRET999 password=parola" };
        var e = Entry(s);
        Assert.AreEqual(SourceHealthBand.Unsupported, e.Band, "User information in the address is what the reader refuses.");
        var visible = e.Row.Summary + " " + e.Detail + " " + e.Row.MaskedLocation;
        Assert.IsFalse(visible.Contains("SECRET999") || visible.Contains("parola"), visible);
        Assert.IsFalse(XmlSourceListGrouping.Matches(e, new(null, "SECRET999")), "Searching for the secret finds nothing.");
        Assert.IsTrue(XmlSourceListGrouping.Matches(e, new(null, "g.example.com")), "…but the host still finds it.");

        var down = Entry(new XmlSource { Name = "Down", Location = "https://d.example.com/f.xml", LastHealthState = "SERVER_ERROR", LastHealthError = "<?xml version=\"1.0\"?><error><token>SECRET999</token></error>" });
        Assert.IsFalse(down.Problem.Contains("SECRET999") || down.Problem.Contains("<error"), down.Problem);
        StringAssert.Contains(down.Problem, "sunucu hatası");

        var latest = XmlSourceListGrouping.LatestRuns(new[] { new XmlRunRecord("r2", "a", "Failed", Now.AddMinutes(-1), Now, 0, 0, 0, "x"), new XmlRunRecord("r1", "a", "Completed", Now.AddHours(-1), Now.AddHours(-1), 1, 0, 0, "") });
        Assert.AreEqual("Failed", latest["a"].Status, "Newest-first: the first record per source wins.");
    }
}
