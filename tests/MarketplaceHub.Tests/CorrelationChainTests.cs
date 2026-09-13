using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #883 (AUDIT UX: Correlation deep-link navigation). Audit rows written while an operation is in flight carry its
// correlation id, so an import, the jobs it queued and the orders it touched share one id across modules; an old
// audit store gains the column without losing its rows; a chain is composed as safe event summaries with entity
// links, scoped to the stores this session may open; a missing correlation is an empty view with a note; the deep
// link is a workspace link of its own kind; a restart (a second store instance) reads the same chain.
[TestClass]
public sealed class CorrelationChainTests
{
    [TestMethod]
    public void AuditRowsCarryTheActiveCorrelationAndAChainIsComposedSafelyScopedToTheAllowedStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "correlation-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            // An old audit store without the column: opened, migrated, its rows readable with an empty correlation.
            var file = Path.Combine(root, "audit.db");
            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString()))
            {
                legacy.Open(); using var create = legacy.CreateCommand();
                create.CommandText = "CREATE TABLE AuditEvents(Id TEXT PRIMARY KEY,AtUtc TEXT NOT NULL,Module TEXT NOT NULL,Action TEXT NOT NULL,ProductId TEXT NOT NULL,OrderId TEXT NOT NULL,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,Outcome TEXT NOT NULL,Detail TEXT NOT NULL);INSERT INTO AuditEvents VALUES('old-1','2026-09-01T10:00:00.0000000Z','import','run','','','','','OK','eski kayıt')";
                create.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var audit = new AuditStore(root);
            var old = audit.List(50).Single(a => a.Id == "old-1");
            Assert.AreEqual("", old.Correlation); Assert.AreEqual("eski kayıt", old.Detail);

            // Rows written inside an operation carry its correlation; outside, none; an explicit id is kept; a non-identifier is dropped.
            using (UiActivity.Enter("ImportAsync", "chain-000001"))
            {
                audit.Append(new AuditEvent { Module = "import", Action = "run", Marketplace = "etsy", ShopId = "S1", Outcome = "OK", Detail = "Kaynak okundu; 120 satır. ali@example.com" });
                audit.Append(new AuditEvent { Module = "job", Action = "enqueue", Marketplace = "etsy", ShopId = "S1", ProductId = "p-1", Outcome = "OK", Detail = "Stok işi kuyruğa alındı." });
                audit.Append(new AuditEvent { Module = "order", Action = "ship", Marketplace = "etsy", ShopId = "S1", OrderId = "1001", Outcome = "Failed", Detail = "{\"token\":\"abc\",\"reason\":\"x\"}" });
            }
            audit.Append(new AuditEvent { Module = "system", Action = "idle", Outcome = "OK", Detail = "korelasyonsuz" });
            audit.Append(new AuditEvent { Module = "order", Action = "note", Marketplace = "ebay", ShopId = "E1", OrderId = "2001", Outcome = "OK", Detail = "başka mağaza", Correlation = "chain-000001" });
            audit.Append(new AuditEvent { Module = "ui", Action = "manual", Outcome = "OK", Detail = "elle", Correlation = "manual-0001" });
            audit.Append(new AuditEvent { Module = "ui", Action = "bad", Outcome = "OK", Detail = "geçersiz kimlik", Correlation = "not an id!" });
            Assert.AreEqual("", audit.List(50).Single(a => a.Action == "idle").Correlation); Assert.AreEqual("manual-0001", audit.List(50).Single(a => a.Action == "manual").Correlation); Assert.AreEqual("", audit.List(50).Single(a => a.Action == "bad").Correlation);
            var chain = audit.ListByCorrelation("chain-000001");
            Assert.AreEqual(4, chain.Count); Assert.IsTrue(chain.Zip(chain.Skip(1)).All(p => p.First.AtUtc <= p.Second.AtUtc), "chain order is time order");
            Assert.AreEqual(0, audit.ListByCorrelation("chain-none").Count); Assert.AreEqual(0, audit.ListByCorrelation("../x").Count);

            // The chain, scoped to the etsy store: three events across three modules, the failed one making it "hata", summaries redacted and bodies hidden, entity links for the product and the order, the ebay row counted as hidden.
            var view = CorrelationChain.Compose("chain-000001", chain, new[] { "etsy|S1" }, route => route == "orders" ? "Sipariş ve kargo" : "Ürünler");
            Assert.AreEqual(3, view.Events.Count); CollectionAssert.AreEqual(new[] { "import", "job", "order" }, view.Modules.ToList()); Assert.AreEqual("hata", view.Outcome);
            Assert.AreEqual(1, view.Hidden); StringAssert.Contains(view.Note, "1 kayıt"); Assert.IsNotNull(view.Duration);
            StringAssert.Contains(view.Events[0].Summary, "[pii-email]"); Assert.IsFalse(view.Events[0].Summary.Contains("example.com", StringComparison.Ordinal));
            Assert.AreEqual(StatusTooltip.RawPayloadHidden, view.Events[2].Summary);
            Assert.AreEqual("product", view.Events[1].Entity!.EntityKind); Assert.AreEqual("p-1", view.Events[1].Entity.EntityId);
            Assert.AreEqual("order", view.Events[2].Entity!.EntityKind); Assert.AreEqual("etsy|S1|1001", view.Events[2].Entity.EntityId); Assert.AreEqual("etsy|S1", view.Events[2].Entity.StoreKey); Assert.AreEqual("orders", view.Events[2].Entity.Route);
            StringAssert.Contains(CorrelationChain.Headline(view), "3 olay"); StringAssert.Contains(CorrelationChain.Headline(view), "hata");
            Assert.IsTrue(CorrelationChain.Summarize(new string('x', 500)).Length <= CorrelationChain.MaxSummaryLength);

            // Wrong store: nothing this session may open is shown, the count says why; no allowed list means every store.
            var wrong = CorrelationChain.Compose("chain-000001", chain, new[] { "trendyol|T1" });
            Assert.AreEqual(0, wrong.Events.Count); Assert.AreEqual(4, wrong.Hidden); StringAssert.Contains(wrong.Note, "4 kayıt");
            Assert.AreEqual(4, CorrelationChain.Compose("chain-000001", chain, null).Events.Count);

            // Missing and invalid correlations are empty views with a note, never an exception.
            var missing = CorrelationChain.Compose("chain-none", chain, new[] { "etsy|S1" });
            Assert.IsTrue(missing.IsEmpty); Assert.AreEqual(CorrelationChain.MissingNote, missing.Note); Assert.AreEqual(missing.Note, CorrelationChain.Headline(missing));
            Assert.AreEqual(CorrelationChain.InvalidNote, CorrelationChain.Compose("not an id!", chain, null).Note);
            Assert.AreEqual("", CorrelationChain.SafeId("../x")); Assert.AreEqual("abc-1234", CorrelationChain.SafeId(" abc-1234 ")); Assert.IsFalse(CorrelationChain.IsId("abc"));

            // The deep link is a workspace link of its own kind, scoped to the store; a hostile id is refused.
            var link = CorrelationChain.Link("chain-000001", "etsy|S1");
            Assert.AreEqual("monobridge://correlation/chain-000001?store=etsy%7CS1", link);
            var target = WorkspaceLinks.Parse(link, _ => "Tanılama / audit");
            Assert.IsNotNull(target); Assert.AreEqual("diagnostics", target.Route); Assert.AreEqual("correlation", target.EntityKind); Assert.AreEqual("chain-000001", target.EntityId); Assert.AreEqual("etsy|S1", target.StoreKey);
            Assert.IsNull(WorkspaceLinks.Parse("monobridge://correlation/..%2Fx", _ => "x"));
            Assert.ThrowsException<ArgumentException>(() => CorrelationChain.Link("bad id"));

            // A restart: a second store instance reads the same chain.
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(4, new AuditStore(root).ListByCorrelation("chain-000001").Count);
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
