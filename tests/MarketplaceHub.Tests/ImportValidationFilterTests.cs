using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #825 (DESIGN: Import validation result filters). Severity, field, reason-code and changed-only filters combine;
// the counts describe the filtered set; zero results is a state, not an error; a hundred thousand rows filter as
// flags; and no filter label ever carries a row's value.
[TestClass]
public sealed class ImportValidationFilterTests
{
    static CatalogProduct Clean(string sku, string name = "Kupa", decimal price = 10) => new() { Sku = sku, Name = name, Price = price, Cost = 4, Stock = 3, Currency = "TRY", Description = "Uzun bir açıklama metni burada.", ImageUrls = "https://cdn.example.com/a.jpg" };

    static CatalogProduct With(CatalogProduct p, Action<CatalogProduct> set) { set(p); return p; }

    static IReadOnlyList<ImportValidationRow> Rows(Dictionary<string, CatalogProduct> pool, params CatalogProduct[] preview) =>
        ImportValidationFilter.Evaluate(preview, p => pool.TryGetValue(p.Sku, out var e) ? e : null);

    [TestMethod]
    public void FiltersCombineAndTheCountsDescribeTheFilteredSet()
    {
        var pool = new Dictionary<string, CatalogProduct> { ["A"] = Clean("A"), ["C"] = Clean("C") };
        var rows = Rows(pool,
            Clean("A"),                                  // unchanged, clean
            Clean("B", price: 12),                       // new, clean
            Clean("C", name: ""),                        // changed, blocking (name)
            With(Clean("D", name: ""), p => p.Currency = "X"),// new, blocking (name + currency)
            With(Clean("E"), p => p.Description = ""));       // new, warning (the rule warns on a missing description)

        var all = ImportValidationFilter.Apply(rows, new());
        Assert.AreEqual(5, all.Counts.Matching); Assert.AreEqual(2, all.Counts.Blocking); Assert.AreEqual(1, all.Counts.Warning); Assert.AreEqual(2, all.Counts.Clean); Assert.AreEqual(4, all.Counts.Changed);
        StringAssert.Contains(all.Counts.Label, "5 / 5");

        var blocking = ImportValidationFilter.Apply(rows, new(Severity: SeverityLevel.Blocking));
        CollectionAssert.AreEqual(new[] { 2, 3 }, blocking.MatchingIndices.ToArray());
        Assert.AreEqual(2, blocking.Counts.Matching); Assert.AreEqual(0, blocking.Counts.Warning, "The counts follow the filter.");

        var blockingCurrency = ImportValidationFilter.Apply(rows, new(Severity: SeverityLevel.Blocking, Field: "Satış para birimi"));
        CollectionAssert.AreEqual(new[] { 3 }, blockingCurrency.MatchingIndices.ToArray(), "Severity AND field.");

        var changedWarnings = ImportValidationFilter.Apply(rows, new(Severity: SeverityLevel.Warning, ChangedOnly: true));
        CollectionAssert.AreEqual(new[] { 4 }, changedWarnings.MatchingIndices.ToArray());

        var byCode = ImportValidationFilter.Apply(rows, new(ReasonCode: "content:baslik"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, byCode.MatchingIndices.ToArray(), "A reason code is the rule, not the message.");

        var changedOnly = ImportValidationFilter.Apply(rows, new(ChangedOnly: true));
        Assert.AreEqual(4, changedOnly.Counts.Matching);
        Assert.IsFalse(changedOnly.MatchingIndices.Contains(0), "The unchanged row is out.");
    }

    [TestMethod]
    public void ZeroResultsIsAStateWithTheTotalStillKnown()
    {
        var rows = Rows(new(), Clean("A"), Clean("B"));

        var none = ImportValidationFilter.Apply(rows, new(Severity: SeverityLevel.Blocking));

        Assert.AreEqual(0, none.MatchingIndices.Count);
        Assert.IsTrue(none.IsEmptyResult, "Nothing matched, but there are rows -- the grid says so instead of looking empty.");
        Assert.AreEqual(2, none.Counts.Total);
        StringAssert.Contains(none.Counts.Label, "0 / 2");
        Assert.IsFalse(ImportValidationFilter.Apply(Array.Empty<ImportValidationRow>(), new()).IsEmptyResult, "No rows at all is not a filtered-to-nothing.");
    }

    [TestMethod]
    public void ChangeClassificationComparesWhatTheImportWouldWrite()
    {
        var existing = Clean("A");
        Assert.AreEqual(ImportRowChange.Unchanged, ImportValidationFilter.Classify(Clean("A"), existing));
        Assert.AreEqual(ImportRowChange.Changed, ImportValidationFilter.Classify(Clean("A", price: 11), existing));
        Assert.AreEqual(ImportRowChange.Changed, ImportValidationFilter.Classify(With(Clean("A"), p => p.Stock = 0), existing));
        Assert.AreEqual(ImportRowChange.New, ImportValidationFilter.Classify(Clean("Z"), null));
        Assert.AreEqual("değişen", ImportValidationFilter.ChangeLabel(ImportRowChange.Changed));
    }

    [TestMethod]
    public void FilterOptionsAreRuleNamesAndCodesNeverRowValues()
    {
        var rows = Rows(new(), With(Clean("A", name: ""), p => p.Description = "ali@example.com token=abc123"), With(Clean("B"), p => p.Currency = "SECRET1"));

        var result = ImportValidationFilter.Apply(rows, new());

        Assert.IsTrue(result.FieldOptions.Count > 0 && result.ReasonCodeOptions.Count > 0);
        foreach (var label in result.FieldOptions.Concat(result.ReasonCodeOptions))
            foreach (var value in new[] { "ali@example.com", "abc123", "SECRET1", "Kupa" })
                Assert.IsFalse(label.Contains(value, StringComparison.OrdinalIgnoreCase), $"'{value}' leaked into a filter label: {label}");
        Assert.IsTrue(result.ReasonCodeOptions.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z0-9-]+:[a-z0-9-]+$")), "Codes are stable ASCII: " + string.Join(",", result.ReasonCodeOptions));
        Assert.IsTrue(rows.All(r => r.StatusLabel.Length > 0));
    }

    [TestMethod]
    public void AHundredThousandRowsFilterAsFlags()
    {
        var pool = new Dictionary<string, CatalogProduct>();
        var preview = new List<CatalogProduct>(100_000);
        for (var i = 0; i < 100_000; i++)
        {
            var sku = "S" + i;
            if (i % 2 == 0) pool[sku] = Clean(sku);
            preview.Add(i % 10 == 0 ? Clean(sku, name: "") : i % 3 == 0 ? Clean(sku, price: 99) : Clean(sku));
        }
        var rows = ImportValidationFilter.Evaluate(preview, p => pool.TryGetValue(p.Sku, out var e) ? e : null);

        var watch = Stopwatch.StartNew();
        var blocking = ImportValidationFilter.Apply(rows, new(Severity: SeverityLevel.Blocking));
        var changed = ImportValidationFilter.Apply(rows, new(ChangedOnly: true, Severity: SeverityLevel.Blocking, Field: "Başlık"));
        watch.Stop();

        Assert.AreEqual(10_000, blocking.Counts.Matching);
        Assert.IsTrue(changed.Counts.Matching > 0 && changed.Counts.Matching <= 10_000);
        Assert.AreEqual(100_000, blocking.Counts.Total);
        Assert.IsTrue(watch.ElapsedMilliseconds < 10_000, $"Two filters over 100k rows took {watch.ElapsedMilliseconds} ms; filtering must be a pass over flags.");
    }
}
