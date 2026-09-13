using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #851 (DESIGN: Notification center severity grouping). The ledger: a fingerprint is one alert however often it is
// seen (repeats count, duplicates inside one batch too), a person's acknowledgement survives repeats, an alert a
// sync no longer sees resolves, a resolved one that returns reopens with the acknowledgement cleared, and every
// title and detail is sanitized on the way in. The grouping: open and unacknowledged first by severity, source and
// store; acknowledged apart; resolved as a tail; a headline that counts; a thousand alerts grouped in milliseconds.
[TestClass]
public sealed class NotificationCenterTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static AlertInput Alert(string title, string severity = "WARNING", string source = "products", string channel = "", string shop = "", string detail = "ayrıntı") => new(NotificationStore.Fingerprint(severity, source, channel.Length == 0 ? "" : DashboardStoreFilter.KeyFor(channel, shop), title), severity, channel, shop, title, detail, source);

    [TestMethod]
    public void TheLedgerCountsRepeatsKeepsAcknowledgementsResolvesAndReopensAndSanitizes()
    {
        var root = Path.Combine(Path.GetTempPath(), "alerts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NotificationStore(root);
            var first = store.Sync(new[] { Alert("Kritik stok"), Alert("Kritik stok"), Alert("Sync başarısız", "ERROR", "sync", "etsy", "S1", "Authorization: Bearer synthetic-secret ayse@example.com") }, T0);
            Assert.AreEqual((2, 1, 0, 0), (first.Added, first.Repeated, first.Reopened, first.Resolved), "A duplicate inside one batch is a repeat, not a second alert.");
            var all = store.List(); Assert.AreEqual(2, all.Count);
            var sync = all.Single(a => a.Title == "Sync başarısız");
            Assert.IsFalse(sync.Detail.Contains("synthetic-secret") || sync.Detail.Contains("example.com"), sync.Detail); Assert.AreEqual("etsy|S1", sync.StoreKey); Assert.AreEqual("sync", sync.Source); Assert.AreEqual("ERROR", sync.Severity); Assert.IsTrue(sync.IsOpen);
            Assert.AreEqual(2, all.Single(a => a.Title == "Kritik stok").Occurrences);

            store.Acknowledge(all.Single(a => a.Title == "Kritik stok").Id);
            var second = store.Sync(new[] { Alert("Kritik stok", detail: "3 aktif ürün stokta yok.") }, T0.AddMinutes(5));
            Assert.AreEqual((0, 1, 0, 1), (second.Added, second.Repeated, second.Reopened, second.Resolved), "The stock alert repeats; the sync alert is no longer live, so it resolves.");
            var stock = store.List().Single(a => a.Title == "Kritik stok");
            Assert.IsTrue(stock.Acknowledged && stock.IsOpen); Assert.AreEqual(3, stock.Occurrences); Assert.AreEqual("3 aktif ürün stokta yok.", stock.Detail, "The detail follows the latest sighting.");
            var resolved = store.List().Single(a => a.Title == "Sync başarısız");
            Assert.IsFalse(resolved.IsOpen); Assert.AreEqual(T0.AddMinutes(5), resolved.ResolvedUtc);

            var third = store.Sync(new[] { Alert("Kritik stok"), Alert("Sync başarısız", "ERROR", "sync", "etsy", "S1") }, T0.AddMinutes(10));
            Assert.AreEqual((0, 1, 1, 0), (third.Added, third.Repeated, third.Reopened, third.Resolved));
            var reopened = store.List().Single(a => a.Title == "Sync başarısız");
            Assert.IsTrue(reopened.IsOpen); Assert.AreEqual(1, reopened.Reopened); Assert.IsNull(reopened.ResolvedUtc); Assert.IsFalse(reopened.Acknowledged);
            store.Acknowledge(reopened.Id); store.Sync(Array.Empty<AlertInput>(), T0.AddMinutes(15)); store.Sync(new[] { Alert("Sync başarısız", "ERROR", "sync", "etsy", "S1") }, T0.AddMinutes(20));
            Assert.IsFalse(store.List().Single(a => a.Title == "Sync başarısız").Acknowledged, "A reopen clears the acknowledgement: it is news again.");
            store.Unacknowledge(stock.Id); Assert.IsFalse(store.List().Single(a => a.Title == "Kritik stok").Acknowledged);

            var kept = store.Add("fp-manual", "INFO", "", "", "Elle", "x"); Assert.AreEqual(kept.Id, store.Add("fp-manual", "INFO", "", "", "Elle", "y").Id, "Add keeps its old dedupe contract.");
            Assert.AreEqual(3, new NotificationStore(root).List().Count, "Add never resolves the others.");
            Assert.AreEqual(NotificationStore.Fingerprint("warning", "products", "", " Kritik Stok "), NotificationStore.Fingerprint("WARNING", "Products", "", "kritik stok"), "The fingerprint folds case and space and ignores the detail.");
            Assert.AreNotEqual(NotificationStore.Fingerprint("WARNING", "products", "", "Kritik stok"), NotificationStore.Fingerprint("WARNING", "products", "etsy|S1", "Kritik stok"), "A store is part of the identity.");
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
    }

    [TestMethod]
    public void GroupsOrderBySeveritySourceAndStoreWithAcknowledgedApartAndAThousandInMilliseconds()
    {
        static LocalNotification Row(string title, string severity, string source, string channel, string shop, bool ack = false, string state = "Open", int minutesAgo = 0, int occurrences = 1, int reopened = 0) =>
            new(Guid.NewGuid().ToString("N"), title, severity, channel, shop, title, "d", ack, T0.AddMinutes(-minutesAgo), source, state, occurrences, state == "Open" ? null : T0.AddMinutes(-minutesAgo), reopened);
        var alerts = new[]
        {
            Row("Kritik stok", "WARNING", "products", "", "", minutesAgo: 30),
            Row("Sync başarısız · etsy/push", "ERROR", "sync", "etsy", "S1", minutesAgo: 5, occurrences: 4),
            Row("Sync başarısız · etsy/pull", "ERROR", "sync", "etsy", "S1", minutesAgo: 1),
            Row("XML çalışması başarısız", "ERROR", "xml", "", "", minutesAgo: 2),
            Row("Bağlantı: Trendyol", "WARNING", "trendyol", "trendyol", "T1", ack: true),
            Row("Sipariş stoğu bekliyor", "INFO", "orders", "", ""),
            Row("Eski uyarı", "WARNING", "products", "", "", state: "Resolved", minutesAgo: 60, reopened: 2),
        };
        var view = NotificationCenter.Build(alerts, T0);
        CollectionAssert.AreEqual(new[] { "Hata · Sync merkezi · etsy · S1", "Hata · XML kaynakları", "Uyarı · Ürünler", "Bilgi · Siparişler" }, view.Unresolved.Select(g => g.Title).ToArray(), "Severity first, then the source label, then the store.");
        var syncGroup = view.Unresolved[0];
        Assert.AreEqual(2, syncGroup.Count); Assert.AreEqual(5, syncGroup.Occurrences); Assert.AreEqual("Sync başarısız · etsy/pull", syncGroup.Alerts[0].Title, "Newest first inside a group.");
        Assert.AreEqual(1, view.Acknowledged.Count); Assert.AreEqual("Uyarı · Trendyol bağlantısı · trendyol · T1", view.Acknowledged[0].Title); Assert.IsTrue(view.Acknowledged[0].Acknowledged);
        Assert.AreEqual(1, view.ResolvedCount); Assert.AreEqual("Eski uyarı", view.Resolved[0].Title);
        Assert.AreEqual("5 açık uyarı: 3 hata, 1 uyarı, 1 bilgi · 1 onaylandı · 1 çözüldü", view.Headline);
        Assert.AreEqual("Açık uyarı yok", NotificationCenter.Build(Array.Empty<LocalNotification>(), T0).Headline);
        Assert.AreEqual("Açık uyarı yok · 1 onaylandı", NotificationCenter.Build(new[] { Row("x", "INFO", "orders", "", "", ack: true) }, T0).Headline);

        var live = new DashboardNotification("Uyarı", "Bağlantı: Etsy S1", "Durum: NOT_CONFIGURED · token=abc", "etsy", "etsy|S1");
        Assert.IsTrue(NotificationCenter.IsAlert(live)); Assert.IsFalse(NotificationCenter.IsAlert(new DashboardNotification("Başarılı", "Açık uyarı yok", "", "dashboard")));
        var input = NotificationCenter.ToAlert(live);
        Assert.AreEqual(("WARNING", "etsy", "etsy", "S1"), (input.Severity, input.Source, input.Channel, input.ShopId)); Assert.AreEqual(NotificationStore.Fingerprint("WARNING", "etsy", "etsy|S1", "Bağlantı: Etsy S1"), input.Fingerprint);
        Assert.AreEqual("Etsy bağlantısı", NotificationCenter.SourceLabel("etsy")); Assert.AreEqual("Veri kalitesi", NotificationCenter.SourceLabel("data-quality")); Assert.AreEqual("Genel", NotificationCenter.SourceLabel("")); Assert.AreEqual("Hata", NotificationCenter.SeverityWord("hata")); Assert.AreEqual(0, NotificationCenter.Rank("Hata"));

        var many = Enumerable.Range(0, 1000).Select(i => Row($"Uyarı {i:D4}", i % 3 == 0 ? "ERROR" : i % 3 == 1 ? "WARNING" : "INFO", i % 5 == 0 ? "sync" : "products", i % 2 == 0 ? "etsy" : "", i % 2 == 0 ? $"S{i % 7}" : "", ack: i % 10 == 0, state: i % 25 == 0 ? "Resolved" : "Open", minutesAgo: i)).ToList();
        var clock = Stopwatch.StartNew(); var big = NotificationCenter.Build(many, T0); clock.Stop();
        Assert.AreEqual(1000, big.Unresolved.Sum(g => g.Count) + big.Acknowledged.Sum(g => g.Count) + big.ResolvedCount);
        Assert.IsTrue(big.Unresolved.Zip(big.Unresolved.Skip(1)).All(p => NotificationCenter.Rank(p.First.Severity) <= NotificationCenter.Rank(p.Second.Severity)), "Groups never go back up in severity.");
        Assert.AreEqual(NotificationCenter.MaxResolvedShown, big.Resolved.Count); Assert.IsTrue(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
    }
}
