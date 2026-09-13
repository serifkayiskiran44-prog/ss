using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #820 (DESIGN: Form-level validation summary). Before a save, every finding is summarised at the top of the form
// and linked to the input that fixes it; the first blocking one is what gets focus; a clean record clears it all;
// and a message never carries what the operator typed.
[TestClass]
public sealed class FormValidationSummaryTests
{
    static CatalogProduct Broken() => new() { Sku = "", Barcode = "", Name = "", Price = -1, Cost = -2, Stock = -3, Currency = "SECRET1234" };

    [TestMethod]
    public void TheFirstBlockingFindingInFormOrderIsTheOneToFocus()
    {
        var view = FormValidationSummary.Compose(ProductValidation.Evaluate(Broken()), FormValidationSummary.ProductPropertyByField);

        Assert.IsFalse(view.CanSave, "Blocking findings refuse the save.");
        Assert.IsNotNull(view.FirstBlocking);
        Assert.AreEqual(SeverityLevel.Blocking, view.FirstBlocking!.Level);
        Assert.IsTrue(view.FirstBlocking.CanFocus, "The first blocking finding is one the form can focus.");
        Assert.AreEqual(view.Links.First(l => l.Level == SeverityLevel.Blocking).Property, view.FirstBlocking.Property, "First in the form's own order, not by severity of message.");
        Assert.AreEqual("İlk engele git", view.Aggregate.CallToAction);
    }

    [TestMethod]
    public void FindingsAcrossSectionsAreAllListedBlockingBeforeWarnings()
    {
        var view = FormValidationSummary.Compose(ProductValidation.Evaluate(Broken()), FormValidationSummary.ProductPropertyByField);

        var sections = view.Links.Where(l => l.Level == SeverityLevel.Blocking).Select(l => l.Section).Distinct().ToList();
        Assert.IsTrue(sections.Count >= 2, "Identity, content and price-stock all carry blockers here: " + string.Join(",", sections));
        var levels = view.Links.Select(l => l.Level).ToList();
        var lastBlocking = levels.LastIndexOf(SeverityLevel.Blocking);
        var firstWarning = levels.IndexOf(SeverityLevel.Warning);
        Assert.IsTrue(firstWarning < 0 || lastBlocking < firstWarning, "Every blocking link precedes every warning link.");
        Assert.IsFalse(view.Links.Any(l => l.Level == SeverityLevel.Info), "Information is not a thing to fix.");
        StringAssert.Contains(view.Aggregate.Headline, "engel");
    }

    [TestMethod]
    public void EveryBlockingRuleTheStoreEnforcesResolvesToAnInputTheFormCanFocus()
    {
        var blocking = ProductValidation.Evaluate(Broken()).Findings.Where(f => f.Severity == ProductValidation.Blocking).ToList();
        Assert.IsTrue(blocking.Count >= 4);
        foreach (var finding in blocking)
            Assert.IsTrue(FormValidationSummary.ProductPropertyByField.ContainsKey(finding.Field), $"'{finding.Field}' has no input to focus; the map is stale.");
    }

    [TestMethod]
    public void AFixedRecordClearsTheSummaryAndAllowsTheSave()
    {
        var view = FormValidationSummary.Compose(ProductValidation.Evaluate(new CatalogProduct { Sku = "A", Name = "Kupa", Price = 10, Cost = 4, Stock = 3, Currency = "TRY", Description = "Uzun bir açıklama metni burada.", ImageUrls = "https://cdn.example.com/a.jpg" }), FormValidationSummary.ProductPropertyByField);

        Assert.IsTrue(view.CanSave);
        Assert.IsNull(view.FirstBlocking);
        Assert.IsFalse(view.Links.Any(l => l.Level == SeverityLevel.Blocking));
        Assert.AreEqual("", view.Aggregate.CallToAction == "İlk engele git" ? "blocking" : "", "No blocker, no blocking call to action.");
    }

    [TestMethod]
    public void AMessageNeverCarriesWhatTheOperatorTyped()
    {
        var entered = new Dictionary<string, string> { ["Currency"] = "SECRET1234", ["Name"] = "" };
        var crafted = new ProductValidationResult(new[]
        {
            new ProductValidationFinding(ProductValidation.Blocking, "price-stock", "Satış para birimi", "'SECRET1234' geçerli bir para birimi değil."),
            new ProductValidationFinding(ProductValidation.Blocking, "content", "Başlık", "Ürün adı zorunlu."),
        });

        var view = FormValidationSummary.Compose(crafted, FormValidationSummary.ProductPropertyByField, entered);

        Assert.IsFalse(view.Links.Any(l => l.Message.Contains("SECRET1234")), "The typed value is masked in the link.");
        Assert.IsFalse(view.Aggregate.Headline.Contains("SECRET1234"), "…and in the headline.");
        Assert.AreEqual("Ürün adı zorunlu.", view.Links.Single(l => l.Property == "Name").Message, "An empty entered value masks nothing.");

        var real = FormValidationSummary.Compose(ProductValidation.Evaluate(Broken()), FormValidationSummary.ProductPropertyByField, new Dictionary<string, string> { ["Currency"] = "SECRET1234" });
        Assert.IsFalse(real.Links.Any(l => l.Message.Contains("SECRET1234")), "The real rules never echo a value either.");
    }

    [TestMethod]
    public void AFindingTheFormDoesNotOwnIsListedWithoutAFocusTarget()
    {
        var crafted = new ProductValidationResult(new[] { new ProductValidationFinding(ProductValidation.Blocking, "channel", "Kanal planı", "Kanal planı eksik.") });

        var view = FormValidationSummary.Compose(crafted, FormValidationSummary.ProductPropertyByField);

        Assert.AreEqual(1, view.Links.Count, "Not dropped.");
        Assert.IsFalse(view.Links[0].CanFocus, "…but nothing to focus.");
        Assert.IsNotNull(view.FirstBlocking, "It still blocks the save.");
        Assert.IsFalse(view.CanSave);
    }
}
