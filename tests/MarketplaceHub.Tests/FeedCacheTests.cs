using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #894 (SOURCE CACHE: feed download retention policy). Successful and failed feeds are kept per source under a
// keep policy, the last known good is a pointer retention never evicts, a size quota evicts the oldest across
// sources, a corrupt entry is never served, orphans of deleted sources and unknown files go, a source with a run in
// progress and a young temporary file are left alone, the cache survives a restart, and an address is kept only as
// a label with its query string dropped and its user segment masked.
[TestClass]
public sealed class FeedCacheTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
    static string Feed(int i) => "<Products><Product><Code>S" + i + "</Code><Title>Ürün " + i + " " + new string('x', 400) + "</Title></Product></Products>";

    [TestMethod]
    public void KeepsAFewPerSourcePointsAtTheLastKnownGoodAndSurvivesARestart()
    {
        var root = NewRoot();
        try
        {
            for (var i = 1; i <= 7; i++) FeedCache.Store(root, "src-a", Feed(i), FeedCacheOutcome.Success, runId: "run" + i, location: "https://feed.example.com/a.xml?token=abc123", nowUtc: T0.AddMinutes(i));
            for (var i = 1; i <= 3; i++) FeedCache.Store(root, "src-a", "<broken>" + i, FeedCacheOutcome.Failed, nowUtc: T0.AddMinutes(20 + i));
            var entries = FeedCache.List(root, "src-a");
            Assert.AreEqual(FeedCache.KeepSuccessPerSource, entries.Count(e => e.Outcome == FeedCacheOutcome.Success), "the oldest successes were trimmed");
            Assert.AreEqual(FeedCache.KeepFailedPerSource, entries.Count(e => e.Outcome == FeedCacheOutcome.Failed));
            Assert.IsTrue(entries.Where(e => e.Outcome == FeedCacheOutcome.Success).All(e => e.LocationLabel == "https://feed.example.com/a.xml"), "the query string never lands in the manifest: " + string.Join(" | ", entries.Select(e => e.LocationLabel)));
            Assert.IsTrue(entries.Where(e => e.Outcome == FeedCacheOutcome.Failed).All(e => e.LocationLabel == ""), "an entry stored without an address has no label");
            Assert.IsFalse(File.ReadAllText(Path.Combine(FeedCache.Root(root), "src-a", FeedCache.ManifestName)).Contains("abc123"));
            var lkg = FeedCache.LastKnownGood(root, "src-a")!;
            Assert.AreEqual("run7", lkg.RunId); StringAssert.Contains(FeedCache.ReadLastKnownGood(root, "src-a")!, "<Code>S7</Code>");
            Assert.AreEqual(Directory.EnumerateFiles(Path.Combine(FeedCache.Root(root), "src-a"), "*.xml").Count(), entries.Count, "no file outside the manifest");
            // Restart: a new reader sees the same manifest.
            Assert.AreEqual("run7", FeedCache.LastKnownGood(root, "src-a")!.RunId);
            Assert.ThrowsException<ArgumentException>(() => FeedCache.Store(root, "../etc", "<x/>", FeedCacheOutcome.Success));
            Assert.AreEqual("", FeedCache.LocationLabel(null)); Assert.AreEqual("https://x.example.com/f.xml", FeedCache.LocationLabel("https://x.example.com/f.xml#frag"));
            StringAssert.Contains(FeedCache.LocationLabel(@"C:\Users\serif\feed.xml"), "[user]");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ACorruptEntryIsNeverServedAndTheSweepRemovesOrphansAndCorruption()
    {
        var root = NewRoot();
        try
        {
            FeedCache.Store(root, "src-a", Feed(1), FeedCacheOutcome.Success, nowUtc: T0);
            var newest = FeedCache.Store(root, "src-a", Feed(2), FeedCacheOutcome.Success, nowUtc: T0.AddMinutes(1));
            var directory = Path.Combine(FeedCache.Root(root), "src-a");
            File.WriteAllText(Path.Combine(directory, newest.FileName), "<tampered/>", Encoding.UTF8);
            var lkg = FeedCache.LastKnownGood(root, "src-a")!;
            StringAssert.Contains(FeedCache.ReadLastKnownGood(root, "src-a")!, "<Code>S1</Code>"); Assert.AreNotEqual(newest.FileName, lkg.FileName, "the tampered newest is skipped");
            Assert.IsFalse(File.Exists(Path.Combine(directory, newest.FileName)), "and dropped");
            // Orphans: an unknown file, a directory of a deleted source, an entry whose file is gone, a corrupt manifest.
            File.WriteAllText(Path.Combine(directory, "stray.xml"), "<x/>");
            Directory.CreateDirectory(Path.Combine(FeedCache.Root(root), "src-gone")); File.WriteAllText(Path.Combine(FeedCache.Root(root), "src-gone", "old.xml"), "<x/>");
            var third = FeedCache.Store(root, "src-a", Feed(3), FeedCacheOutcome.Success, nowUtc: T0.AddMinutes(2)); File.Delete(Path.Combine(directory, third.FileName));
            Directory.CreateDirectory(Path.Combine(FeedCache.Root(root), "src-b")); File.WriteAllText(Path.Combine(FeedCache.Root(root), "src-b", FeedCache.ManifestName), "not json");
            var report = FeedCache.Sweep(root, new[] { "src-a", "src-b" }, nowUtc: T0.AddHours(1));
            Assert.AreEqual(3, report.Orphans, "the stray file, the gone source's directory and the missing entry: " + report);
            Assert.AreEqual(1, report.Corrupt, "the unreadable manifest is counted");
            Assert.IsFalse(Directory.Exists(Path.Combine(FeedCache.Root(root), "src-gone"))); Assert.IsFalse(File.Exists(Path.Combine(directory, "stray.xml")));
            Assert.AreEqual(1, FeedCache.List(root, "src-a").Count); Assert.AreEqual(1, report.Kept);
            Assert.AreEqual("OK", FeedCache.Check(root).Status); StringAssert.Contains(FeedCache.Check(root).Detail, "kayıt");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void QuotaPressureEvictsTheOldestButNeverALastKnownGoodOrAnActiveRunsEntries()
    {
        var root = NewRoot();
        try
        {
            var sizes = new[] { "src-a", "src-b", "src-c" };
            foreach (var (id, i) in sizes.Select((id, i) => (id, i)))
            {
                FeedCache.Store(root, id, Feed(1) + new string('a', 2000), FeedCacheOutcome.Success, nowUtc: T0.AddMinutes(i));
                FeedCache.Store(root, id, Feed(2) + new string('b', 2000), FeedCacheOutcome.Success, nowUtc: T0.AddMinutes(10 + i));
                FeedCache.Store(root, id, "<broken>" + new string('c', 2000), FeedCacheOutcome.Failed, nowUtc: T0.AddMinutes(20 + i));
            }
            var before = FeedCache.Sweep(root, sizes, nowUtc: T0.AddHours(1));
            Assert.AreEqual(9, before.Kept); Assert.AreEqual(0, before.Evicted);
            // A quota for about four entries: failed first, then the oldest successes; every source keeps its last known good; the active source keeps everything.
            var quota = before.Bytes * 4 / 9;
            var report = FeedCache.Sweep(root, sizes, activeSourceIds: new[] { "src-c" }, quotaBytes: quota, nowUtc: T0.AddHours(1));
            Assert.IsTrue(report.Bytes <= quota || report.Kept == 3 + 2, $"under the quota or only the protected left: {report}");
            Assert.IsTrue(report.Evicted >= 3, "at least the two evictable failed entries and an old success went: " + report);
            Assert.AreEqual(3, FeedCache.List(root, "src-c").Count, "an active source is not touched");
            foreach (var id in new[] { "src-a", "src-b" })
            {
                var lkg = FeedCache.LastKnownGood(root, id)!; StringAssert.Contains(FeedCache.ReadLastKnownGood(root, id)!, "<Code>S2</Code>");
                Assert.IsFalse(FeedCache.List(root, id).Any(e => e.Outcome == FeedCacheOutcome.Failed), "failed entries go first");
            }
            Assert.IsTrue(FeedCache.Check(root, quotaBytes: 1).Status == "WARN", "over the quota is a warning");
            // A young temporary file of a write in flight is left alone; an old one is an orphan.
            var directory = Path.Combine(FeedCache.Root(root), "src-a");
            var young = Path.Combine(directory, "x.xml" + FeedCache.TemporaryMarker + "1234"); File.WriteAllText(young, "<x/>");
            var old = Path.Combine(directory, "y.xml" + FeedCache.TemporaryMarker + "5678"); File.WriteAllText(old, "<x/>"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            var sweep = FeedCache.Sweep(root, sizes, nowUtc: DateTime.UtcNow);
            Assert.IsTrue(File.Exists(young), "a write in flight is not an orphan"); Assert.IsFalse(File.Exists(old), "an abandoned temporary is");
            Assert.AreEqual(1, sweep.Orphans);
        }
        finally { Cleanup(root); }
    }

    static string NewRoot() { var root = Path.Combine(Path.GetTempPath(), "feed-cache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
