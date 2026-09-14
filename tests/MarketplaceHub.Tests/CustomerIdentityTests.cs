using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #943 (ORDER CUSTOMER: order–customer relationship integrity). An order's customer snapshot is linked, within the
// store's scope, to one canonical customer keyed by the e-mail (else the phone): the first snapshot creates it, later
// ones find it, a look-alike under another identity is flagged a duplicate candidate and never merged, an unkeyed
// snapshot gets no record; a later change of the canonical record never rewrites an order's snapshot; every word is
// masked; it all reads back after a restart; the real customer save links by itself.
[TestClass]
public sealed class CustomerIdentityTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static OrderCustomer Customer(string shop, string order, string name, string email, string phone, string address) => new("etsy", shop, order, name, email, phone, address);

    [TestMethod]
    public void TheFirstSnapshotCreatesTheCanonicalLaterOnesFindItLookAlikesAreFlaggedNotMergedAndUnkeyedOnesGetNoRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CustomerIdentityStore(root);

            // New: the first order's snapshot creates the canonical record; the words are masked, the key abbreviated.
            var first = store.Link(Customer("S1", "o-1", "Ayşe Yılmaz", "Ayse.Yilmaz@Example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy"), Now);
            Assert.AreEqual(CustomerLink.New, first.Outcome); Assert.AreEqual(16, first.CustomerKey.Length); StringAssert.Contains(first.Words, "yeni kanonik müşteri " + first.CustomerKey[..8]);
            Assert.IsFalse(first.Words.Contains("Yılmaz", StringComparison.Ordinal) || first.Words.Contains("example.com", StringComparison.OrdinalIgnoreCase) || first.Words.Contains("45 67", StringComparison.Ordinal) || first.Words.Contains("Kadıköy", StringComparison.Ordinal), first.Words);
            StringAssert.Contains(first.Words, "A••• Y••• · a••@e•••.com · +•••••••••67 · Bağdat •••");
            var canonical = store.Get("etsy", "S1", first.CustomerKey)!; Assert.AreEqual((1, 1, "Ayşe Yılmaz"), (canonical.Version, canonical.Orders, canonical.Name));

            // Existing: the same e-mail in another case and spacing on a second order finds the record and counts the order; a relink of the same order counts nothing twice.
            var second = store.Link(Customer("S1", "o-2", "Ayse Yilmaz", "  ayse.yilmaz@example.com ", "", "Başka adres"), Now.AddHours(1));
            Assert.AreEqual((CustomerLink.Existing, first.CustomerKey), (second.Outcome, second.CustomerKey)); StringAssert.Contains(second.Words, "mevcut kanonik müşteri"); StringAssert.Contains(second.Words, "(2 sipariş, sürüm 1)");
            Assert.AreEqual(2, store.Get("etsy", "S1", first.CustomerKey)!.Orders); Assert.AreEqual("Ayşe Yılmaz", store.Get("etsy", "S1", first.CustomerKey)!.Name, "a later snapshot does not rewrite the canonical record");
            Assert.AreEqual(CustomerLink.Existing, store.Link(Customer("S1", "o-2", "Ayse Yilmaz", "ayse.yilmaz@example.com", "", "Başka adres"), Now.AddHours(2)).Outcome); Assert.AreEqual(2, store.Get("etsy", "S1", first.CustomerKey)!.Orders, "a relink is idempotent");

            // Duplicate candidate: a new e-mail with the same phone gets its own record and a flag naming the candidate; never merged.
            var lookAlike = store.Link(Customer("S1", "o-3", "A. Yılmaz", "ayse2@example.com", "0532 123 45 67", "Yeni adres"), Now.AddHours(3));
            Assert.AreEqual((CustomerLink.DuplicateCandidate, first.CustomerKey), (lookAlike.Outcome, lookAlike.CandidateKey)); Assert.AreNotEqual(first.CustomerKey, lookAlike.CustomerKey); StringAssert.Contains(lookAlike.Words, "olası mükerrer: " + first.CustomerKey[..8]); StringAssert.Contains(lookAlike.Words, "birleştirme otomatik değildir");
            Assert.AreEqual(2, store.List("etsy", "S1").Count, "two canonical records, no merge"); Assert.AreEqual("o-3", store.Candidates("etsy", "S1").Single().OrderId);
            var sameNameAddress = store.Link(Customer("S1", "o-4", "Ayşe Yılmaz", "", "0555 000 00 00", "Bağdat Cad. 12/3 Kadıköy"), Now.AddHours(4));
            Assert.AreEqual((CustomerLink.DuplicateCandidate, first.CustomerKey), (sameNameAddress.Outcome, sameNameAddress.CandidateKey), "the same name and address under a phone identity is a candidate too");

            // Unkeyed: neither e-mail nor a usable phone -- no canonical record, said so; the store scope: the same e-mail on another shop is another customer.
            var unkeyed = store.Link(Customer("S1", "o-5", "Misafir", "", "12", "—"), Now.AddHours(5)); Assert.AreEqual((CustomerLink.Unkeyed, ""), (unkeyed.Outcome, unkeyed.CustomerKey)); StringAssert.Contains(unkeyed.Words, "kanonik müşteri kaydı oluşturulmadı");
            var otherShop = store.Link(Customer("S2", "o-1", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "", ""), Now.AddHours(6)); Assert.AreEqual(CustomerLink.New, otherShop.Outcome); Assert.AreNotEqual(first.CustomerKey, otherShop.CustomerKey);
            Assert.IsTrue(new AuditStore(root).List(20).Any(a => a.Action == "customer-link" && a.Outcome == "Warning" && a.Detail.Contains("olası mükerrer", StringComparison.Ordinal) && !a.Detail.Contains("example", StringComparison.OrdinalIgnoreCase)), "the duplicate candidate is audited by key prefix only");

            // Restart: links and records read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CustomerIdentityStore(root);
            Assert.AreEqual((first.CustomerKey, CustomerLink.Existing), (reopened.LinkOf("etsy", "S1", "o-2")!.CustomerKey, reopened.LinkOf("etsy", "S1", "o-2")!.Outcome)); Assert.AreEqual(3, reopened.List("etsy", "S1").Count); Assert.AreEqual(2, reopened.Candidates("etsy", "S1").Count);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheRealCustomerSaveLinksByItselfAndAChangedCanonicalRecordLeavesTheOrderSnapshotAsItWas()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-id-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var identities = new CustomerIdentityStore(root);
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });
            orders.SaveCustomer(new OrderCustomer("etsy", "S1", "o-1", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy"));
            orders.SaveCustomer(new OrderCustomer("etsy", "S1", "o-2", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy"));

            // The saves linked both orders to one canonical record.
            var link1 = identities.LinkOf("etsy", "S1", "o-1")!; var link2 = identities.LinkOf("etsy", "S1", "o-2")!;
            Assert.AreEqual((CustomerLink.New, CustomerLink.Existing, link1.CustomerKey), (link1.Outcome, link2.Outcome, link2.CustomerKey)); Assert.AreEqual(2, identities.Get("etsy", "S1", link1.CustomerKey)!.Orders);

            // The canonical record changes on purpose (a new address, a new version); the orders' snapshots stay what they were placed with.
            var updated = identities.UpdateCanonical("etsy", "S1", link1.CustomerKey, "Ayşe Yılmaz-Demir", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Yeni Mah. 5 Ataşehir", Now);
            Assert.AreEqual((2, "Yeni Mah. 5 Ataşehir"), (updated.Version, updated.Address));
            Assert.AreEqual("Bağdat Cad. 12/3 Kadıköy", orders.ReadCustomer("etsy", "S1", "o-1")!.Address); Assert.AreEqual("Ayşe Yılmaz", orders.ReadCustomer("etsy", "S1", "o-2")!.Name);
            Assert.AreEqual(link1.CustomerKey, identities.LinkOf("etsy", "S1", "o-1")!.CustomerKey, "the link survives the change");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => identities.UpdateCanonical("etsy", "S1", "0000000000000000", "x", "", "", "", Now)).Message, "bulunamadı");

            // A snapshot saved again for the same order relinks to the same record without counting twice; the emptied snapshot is deleted from the order, the link kept as history.
            orders.SaveCustomer(new OrderCustomer("etsy", "S1", "o-1", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy"));
            Assert.AreEqual(2, identities.Get("etsy", "S1", link1.CustomerKey)!.Orders);
            orders.SaveCustomer(new OrderCustomer("etsy", "S1", "o-2", "", "", "", "")); Assert.IsNull(orders.ReadCustomer("etsy", "S1", "o-2")); Assert.IsNotNull(identities.LinkOf("etsy", "S1", "o-2"));

            // Restart: the canonical version and the links read back.
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(2, new CustomerIdentityStore(root).Get("etsy", "S1", link1.CustomerKey)!.Version);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
