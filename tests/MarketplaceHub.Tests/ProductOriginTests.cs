using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #910 (PRODUCT ORIGIN: country of origin with a canonical code, a display name and provenance). The value is kept
// as given and resolved to an ISO 3166-1 alpha-2 code only by exact match (code, alpha-3, or a name in Turkish,
// English or the country's own language); missing and unknown are distinct; the display name follows the UI
// language; every change of the resolved origin is one audit row of codes, never text; it all survives a restart.
[TestClass]
public sealed class ProductOriginTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["CountryOfOrigin"] = "o" } };
    static T Under<T>(string culture, Func<T> f) { var old = CultureInfo.CurrentUICulture; try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture); return f(); } finally { CultureInfo.CurrentUICulture = old; } }

    [TestMethod]
    public void CodesResolveByExactMatchDisplayFollowsTheUiLanguageAndTheStatesNeverBlur()
    {
        // Valid codes: alpha-2 in any case, alpha-3, a Turkish name (dotted and dotless i alike), an English name, a native name.
        Assert.IsTrue(ProductOrigin.IsValidCode("de")); Assert.IsTrue(ProductOrigin.IsValidCode(" TR "));
        Assert.AreEqual("DE", ProductOrigin.ResolveCode("de ")); Assert.AreEqual("DE", ProductOrigin.ResolveCode("DEU")); Assert.AreEqual("DE", ProductOrigin.ResolveCode("Almanya")); Assert.AreEqual("DE", ProductOrigin.ResolveCode("Germany")); Assert.AreEqual("DE", ProductOrigin.ResolveCode("Deutschland"));
        Assert.AreEqual("IT", ProductOrigin.ResolveCode("italya")); Assert.AreEqual("IT", ProductOrigin.ResolveCode("İTALYA")); Assert.AreEqual("TR", ProductOrigin.ResolveCode("Türkiye")); Assert.AreEqual("CN", ProductOrigin.ResolveCode("Çin"));
        // Invalid: a UN area number, a language code, a continent, junk -- nothing guessed.
        foreach (var bad in new[] { "XX", "150", "EN", "Avrupa", "Atlantis", "D", "" }) { Assert.IsFalse(ProductOrigin.IsValidCode(bad), bad); Assert.AreEqual("", ProductOrigin.ResolveCode(bad), bad); }

        // Localized display: the Turkish table for a Turkish UI, the English name for another UI and for a country the table lacks.
        Assert.AreEqual("Almanya", ProductOrigin.DisplayName("DE", "tr-TR")); Assert.AreEqual("Germany", ProductOrigin.DisplayName("DE", "en-US")); Assert.AreEqual("Çin", ProductOrigin.DisplayName("cn", "tr"));
        Assert.AreEqual(new RegionInfo("TV").EnglishName, ProductOrigin.DisplayName("TV", "tr-TR"), "a country the table lacks shows its English name"); Assert.AreEqual("", ProductOrigin.DisplayName("XX", "tr-TR"));
        Assert.AreEqual("Deutschland", ProductOrigin.NativeName("DE"));

        // Missing, known, unknown -- three states with their words; the value as given stays beside the code.
        var missing = new CatalogProduct { Sku = "M", Name = "Ürün", Currency = "TRY" };
        var known = new CatalogProduct { Sku = "K", Name = "Ürün", Currency = "TRY", CountryOfOrigin = " Almanya " };
        var unknown = new CatalogProduct { Sku = "U", Name = "Ürün", Currency = "TRY", CountryOfOrigin = "Atlantis" };
        Assert.AreEqual(OriginResolution.Missing, ProductOrigin.Resolve(missing).State); StringAssert.Contains(ProductOrigin.Resolve(missing).Words, "belirtilmedi");
        Assert.IsTrue(ProductOrigin.Apply(known)); Assert.AreEqual("Almanya", known.CountryOfOrigin); Assert.AreEqual("DE", known.CountryOfOriginCode);
        var resolved = Under("tr-TR", () => ProductOrigin.Resolve(known)); Assert.AreEqual(OriginResolution.Known, resolved.State); Assert.AreEqual("DE", resolved.Code); Assert.AreEqual("Almanya", resolved.Display); StringAssert.Contains(resolved.Words, "Deutschland");
        Assert.AreEqual("Germany", Under("en-US", () => ProductOrigin.Resolve(known)).Display);
        Assert.IsFalse(ProductOrigin.Apply(unknown), "no code before, none after"); Assert.AreEqual("", unknown.CountryOfOriginCode); Assert.AreEqual("Atlantis", unknown.CountryOfOrigin);
        var odd = ProductOrigin.Resolve(unknown); Assert.AreEqual(OriginResolution.Unknown, odd.State); StringAssert.Contains(odd.Words, "tanınmıyor");
        Assert.IsFalse(ProductOrigin.Apply(missing)); Assert.AreEqual(OriginResolution.Missing, ProductOrigin.Resolve(missing).State);

        // Changes and their audit rows: codes only, never the text -- a hostile value leaves no trace.
        var before = ProductOrigin.Snapshot(known);
        known.CountryOfOrigin = "https://evil.example/?token=abc123&x=1"; ProductOrigin.Apply(known);
        var change = ProductOrigin.Change(before, known)!; Assert.AreEqual("DE", change.FromCode); Assert.AreEqual("?", change.ToCode);
        var audit = ProductOrigin.ToAudit(known.Id, change, FieldProvenance.ManualKind);
        Assert.AreEqual("catalog", audit.Module); Assert.AreEqual(ProductOrigin.ChangeAction, audit.Action); Assert.AreEqual("Warning", audit.Outcome); Assert.AreEqual("Menşei DE → tanınmıyor · elle", audit.Detail);
        Assert.IsFalse(ProductOrigin.Resolve(known).Words.Contains("abc123"), "the drawer words redact the value"); Assert.IsFalse(audit.Detail.Contains("evil"));
        known.CountryOfOrigin = "tr"; ProductOrigin.Apply(known); Assert.AreEqual("Menşei tanınmıyor → TR · kaynaktan", ProductOrigin.ToAudit(known.Id, ProductOrigin.Change(ProductOrigin.Snapshot(unknown), known)!, FieldProvenance.FeedKind).Detail);
        Assert.IsNull(ProductOrigin.Change(ProductOrigin.Snapshot(known), known), "no move, no change");
        known.CountryOfOrigin = "TUR"; ProductOrigin.Apply(known); Assert.IsNull(ProductOrigin.Change(ProductOrigin.Snapshot(new CatalogProduct { CountryOfOrigin = "Türkiye", CountryOfOriginCode = "TR" }), known), "the same country written another way is not a change");

        // Validation and the drawer.
        Assert.IsTrue(ProductValidation.Evaluate(unknown).Findings.Any(f => f.Field == "Menşei" && f.Severity == ProductValidation.Warning));
        Assert.IsFalse(ProductValidation.Evaluate(missing).Findings.Any(f => f.Field == "Menşei"), "not specified is the ordinary state");
        var row = Under("tr-TR", () => ProductQuickInspect.Build(known, Array.Empty<SyncJob>(), Now).Rows.Single(r => r.Label == "Menşei"));
        Assert.AreEqual("Kimlik", row.Section); StringAssert.Contains(row.Value, "TR · Türkiye");
    }

    [TestMethod]
    public void TheStoreKeepsTheOriginItsCodeItsProvenanceAndAnAuditRowPerChangeAcrossFeedsSavesAndARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "origin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A"); store.SaveSource(a);
            void Import((string Sku, string Origin)[] rows, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                try
                {
                    store.Import(a, rows.Select(r => new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, CountryOfOrigin = r.Origin }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }
            IReadOnlyList<AuditEvent> OriginAudits() => new AuditStore(root).List(100).Where(e => e.Action == ProductOrigin.ChangeAction).ToList();

            // The feed's name resolves to a code; the feed is the field's origin; a new record is no change.
            Import([("SKU-1", "Almanya"), ("SKU-2", "")], Now);
            var one = store.Products().Single(p => p.Sku == "SKU-1"); var two = store.Products().Single(p => p.Sku == "SKU-2");
            Assert.AreEqual("DE", one.CountryOfOriginCode); Assert.AreEqual("Almanya", one.CountryOfOrigin); Assert.AreEqual(FieldProvenance.FeedKind, FieldProvenance.Of(one, "CountryOfOrigin")!.Kind); Assert.AreEqual(a.Id, FieldProvenance.Of(one, "CountryOfOrigin")!.SourceId);
            Assert.AreEqual(OriginResolution.Missing, ProductOrigin.Resolve(two).State); Assert.AreEqual(0, OriginAudits().Count);

            // The source changes the country: the code moves and one audit row names both codes and the feed.
            Import([("SKU-1", "China"), ("SKU-2", "Atlantis")], Now.AddMinutes(10));
            var moved = store.FindProduct(one.Id)!; Assert.AreEqual("CN", moved.CountryOfOriginCode); Assert.AreEqual("China", moved.CountryOfOrigin);
            var odd = store.FindProduct(two.Id)!; Assert.AreEqual("", odd.CountryOfOriginCode); Assert.AreEqual("Atlantis", odd.CountryOfOrigin); Assert.AreEqual(OriginResolution.Unknown, ProductOrigin.Resolve(odd).State);
            Assert.IsTrue(ProductValidation.Evaluate(odd).Findings.Any(f => f.Field == "Menşei" && f.Severity == ProductValidation.Warning));
            var audits = OriginAudits(); Assert.AreEqual(2, audits.Count);
            Assert.AreEqual("Menşei DE → CN · kaynaktan", audits.Single(e => e.ProductId == one.Id).Detail); Assert.AreEqual("Menşei belirtilmedi → tanınmıyor · kaynaktan", audits.Single(e => e.ProductId == two.Id).Detail); Assert.AreEqual("Warning", audits.Single(e => e.ProductId == two.Id).Outcome);

            // The operator writes a code: canonical, theirs, one more audit row; a save that does not move the origin writes none.
            var edit = store.FindProduct(one.Id)!; edit.CountryOfOrigin = " tr "; store.SaveProduct(edit);
            var mine = store.FindProduct(one.Id)!; Assert.AreEqual("TR", mine.CountryOfOriginCode); Assert.AreEqual("tr", mine.CountryOfOrigin); Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(mine, "CountryOfOrigin")!.Kind);
            Assert.AreEqual("Menşei CN → TR · elle", OriginAudits().First(e => e.ProductId == one.Id).Detail);
            var same = store.FindProduct(one.Id)!; same.CountryOfOrigin = "Türkiye"; store.SaveProduct(same);
            Assert.AreEqual(3, OriginAudits().Count, "the same country written another way is not a change"); Assert.AreEqual("TR", store.FindProduct(one.Id)!.CountryOfOriginCode);

            // Restart: the value as given, the code and the audit trail are read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root); var back = reopened.FindProduct(one.Id)!;
            Assert.AreEqual("Türkiye", back.CountryOfOrigin); Assert.AreEqual("TR", back.CountryOfOriginCode); Assert.AreEqual(3, OriginAudits().Count);
            Assert.AreEqual("Türkiye", Under("tr-TR", () => ProductOrigin.Resolve(back)).Display);
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
