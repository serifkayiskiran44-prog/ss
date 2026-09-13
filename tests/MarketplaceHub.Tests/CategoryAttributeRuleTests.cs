using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #915 (TAXONOMY: attribute completeness per category). A local category requires or allows attributes; the taxonomy's
// Attribute entries are an attribute's allowed values when any are defined; a product's "ad=değer; ad=değer" text is
// covered against the rules of its category (by name or approved alias): a required attribute missing or a value
// outside the allowed values makes the product incomplete; an optional one missing is said, not counted; a category
// change changes the rules; an empty product misses everything; no variant engine; it all survives a restart and
// reaches the quality scan.
[TestClass]
public sealed class CategoryAttributeRuleTests
{
    static TaxonomyEntry Entry(TaxonomyStore taxonomy, TaxonomyKind kind, string name, string value = "") => taxonomy.Save(new TaxonomyEntry { Kind = kind, Name = name, Value = value });

    [TestMethod]
    public void CoverageFollowsTheCategorysRulesTheAllowedValuesAndTheProductsText()
    {
        var root = Path.Combine(Path.GetTempPath(), "attr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var rules = new CategoryAttributeRuleStore(root);
            var clothing = Entry(taxonomy, TaxonomyKind.Category, "Giyim"); var books = Entry(taxonomy, TaxonomyKind.Category, "Kitap"); var inactive = Entry(taxonomy, TaxonomyKind.Category, "Eski");
            aliases.Save("giyim ürünleri", clothing.Id, approved: true);
            foreach (var size in new[] { "S", "M", "L" }) Entry(taxonomy, TaxonomyKind.Attribute, "Beden", size);
            rules.Save(clothing.Id, "Beden", required: true); rules.Save(clothing.Id, "Renk", required: true); rules.Save(clothing.Id, "Kumaş", required: false);
            rules.Save(books.Id, "ISBN", required: true);

            // The rules: saved per category, the same attribute again updated in place, refused for a bad name or an inactive category.
            Assert.AreEqual(3, rules.List(clothing.Id).Count); rules.Save(clothing.Id, "kumaş", required: true); Assert.AreEqual(3, rules.List(clothing.Id).Count); Assert.IsTrue(rules.List(clothing.Id).Single(r => r.AttributeKey == CategoryAttributeRuleStore.Key("Kumaş")).Required);
            rules.Save(clothing.Id, "Kumaş", required: false);
            Assert.ThrowsException<InvalidOperationException>(() => rules.Save(clothing.Id, "a=b", true)); Assert.ThrowsException<InvalidOperationException>(() => rules.Save("no-such", "Renk", true));
            inactive.Active = false; taxonomy.Save(inactive); Assert.ThrowsException<InvalidOperationException>(() => rules.Save(inactive.Id, "Renk", true));

            // Parsing: pairs by the attribute's fold, the last value of a repeated name winning, junk ignored.
            var pairs = CategoryAttributeSnapshot.Parse("Beden=M; renk = Kırmızı ;Kumaş=Pamuk; bozuk; =x; Beden=L");
            Assert.AreEqual(3, pairs.Count); Assert.AreEqual("L", pairs[CategoryAttributeRuleStore.Key("beden")].Value); Assert.AreEqual("Kırmızı", pairs[CategoryAttributeRuleStore.Key("Renk")].Value);

            var snapshot = rules.Snapshot();
            // Complete: every required attribute present with an allowed value; an optional one missing is said, not counted.
            var complete = snapshot.Evaluate("GİYİM", "Beden=m; Renk=Kırmızı"); Assert.AreEqual(AttributeCoverage.Complete, complete.Status); Assert.IsFalse(complete.Blocks); CollectionAssert.AreEquivalent(new[] { "Beden", "Renk" }, complete.Present.ToList()); CollectionAssert.AreEqual(new[] { "Kumaş" }, complete.OptionalMissing.ToList()); StringAssert.Contains(complete.Words, "isteğe bağlı eksik: Kumaş");
            Assert.AreEqual(AttributeCoverage.Complete, snapshot.Evaluate("giyim ürünleri", "Beden=S; Renk=Mavi; Kumaş=Keten").Status, "an approved alias finds the category's rules");
            // Required missing: incomplete, the missing named; a required attribute with an empty value is missing.
            var missing = snapshot.Evaluate("Giyim", "Beden=M; Renk="); Assert.AreEqual(AttributeCoverage.Incomplete, missing.Status); Assert.IsTrue(missing.Blocks); CollectionAssert.AreEqual(new[] { "Renk" }, missing.Missing.ToList()); StringAssert.Contains(missing.Words, "eksik zorunlu: Renk");
            // Invalid enum: a value outside the attribute's allowed values, the allowed ones named; a free-text attribute takes any value.
            var invalid = snapshot.Evaluate("Giyim", "Beden=XXL; Renk=Turkuaz"); Assert.AreEqual(AttributeCoverage.Incomplete, invalid.Status); Assert.AreEqual(1, invalid.Invalid.Count); Assert.AreEqual("Beden", invalid.Invalid[0].Attribute); Assert.AreEqual("XXL", invalid.Invalid[0].Value); foreach (var size in new[] { "S", "M", "L" }) StringAssert.Contains(invalid.Invalid[0].Allowed, size); StringAssert.Contains(invalid.Words, "geçersiz değer"); // the sample lists the allowed values in the dictionary's order
            Assert.AreEqual(0, invalid.Missing.Count, "Renk is free text and present");
            // Category change: the same text under another category meets other rules; a category without rules has nothing to cover; no category or an unknown one is said.
            var books1 = snapshot.Evaluate("Kitap", "Beden=M; Renk=Kırmızı"); Assert.AreEqual(AttributeCoverage.Incomplete, books1.Status); CollectionAssert.AreEqual(new[] { "ISBN" }, books1.Missing.ToList());
            Assert.AreEqual(AttributeCoverage.Complete, snapshot.Evaluate("Kitap", "ISBN=978-1").Status);
            Assert.AreEqual(AttributeCoverage.NoRules, snapshot.Evaluate("Eski", "Renk=Mavi").Status); Assert.AreEqual(AttributeCoverage.NoCategory, snapshot.Evaluate("", "Renk=Mavi").Status); Assert.AreEqual(AttributeCoverage.UnknownCategory, snapshot.Evaluate("Mutfak", "Renk=Mavi").Status);
            // Empty product: everything required is missing.
            var empty = snapshot.Evaluate("Giyim", ""); Assert.AreEqual(AttributeCoverage.Incomplete, empty.Status); CollectionAssert.AreEqual(new[] { "Beden", "Renk" }, empty.Missing.ToList()); Assert.AreEqual(0, empty.Present.Count);
            var product = new CatalogProduct { Sku = "P", Name = "Tişört", Currency = "TRY", Category = "Giyim", AttributesText = "Beden=L; Renk=Siyah" }; Assert.AreEqual(AttributeCoverage.Complete, snapshot.Evaluate(product).Status);

            // Restart and removal.
            SqliteConnection.ClearAllPools();
            var reopened = new CategoryAttributeRuleStore(root); Assert.AreEqual(4, reopened.List().Count); Assert.AreEqual(AttributeCoverage.Incomplete, reopened.Snapshot().Evaluate("Giyim", "").Status);
            Assert.IsTrue(reopened.Remove(clothing.Id, "RENK")); Assert.IsFalse(reopened.Remove(clothing.Id, "RENK")); Assert.AreEqual(AttributeCoverage.Complete, reopened.Snapshot().Evaluate("Giyim", "Beden=M").Status);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheQualityScanCarriesTheCoverageOfEveryProductAgainstItsCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), "attr-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var rules = new CategoryAttributeRuleStore(root);
            var clothing = Entry(taxonomy, TaxonomyKind.Category, "Giyim");
            foreach (var size in new[] { "S", "M", "L" }) Entry(taxonomy, TaxonomyKind.Attribute, "Beden", size);
            rules.Save(clothing.Id, "Beden", required: true); rules.Save(clothing.Id, "Renk", required: false);
            static CatalogProduct P(string sku, string category, string attributes) => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = category, AttributesText = attributes };
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { P("SKU-1", "Giyim", "Beden=M"), P("SKU-2", "Giyim", ""), P("SKU-3", "Giyim", "Beden=XL"), P("SKU-4", "Bahçe", ""), P("SKU-5", "", "") });
            string Id(string sku) => store.Products().Single(p => p.Sku == sku).Id;

            var scan = new DataQualityService(root).Scan().Where(i => i.Type is "MissingRequiredAttribute" or "InvalidAttributeValue").ToList();
            Assert.IsFalse(scan.Any(i => i.ProductId == Id("SKU-1")), "complete");
            var missing = scan.Single(i => i.ProductId == Id("SKU-2")); Assert.AreEqual("MissingRequiredAttribute", missing.Type); Assert.AreEqual("Error", missing.Severity); StringAssert.Contains(missing.Message, "Beden");
            var invalid = scan.Single(i => i.ProductId == Id("SKU-3")); Assert.AreEqual("InvalidAttributeValue", invalid.Type); Assert.AreEqual("Error", invalid.Severity); StringAssert.Contains(invalid.Message, "XL");
            Assert.IsFalse(scan.Any(i => i.ProductId == Id("SKU-4")), "a category without rules has nothing to cover"); Assert.IsFalse(scan.Any(i => i.ProductId == Id("SKU-5")), "no category, no rule");

            // The operator fills the attribute in: the next scan finds nothing for it.
            var fixIt = store.FindProduct(Id("SKU-2"))!; fixIt.AttributesText = "Beden=S"; store.SaveProduct(fixIt);
            Assert.IsFalse(new DataQualityService(root).Scan().Any(i => i.Type == "MissingRequiredAttribute" && i.ProductId == Id("SKU-2")));
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
