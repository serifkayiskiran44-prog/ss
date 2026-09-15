using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1963 (named formula profile CRUD/deactivate lifecycle) and
/// #1964 (typed source-field selection, no fabricated field name accepted).
[TestClass]
public sealed class PricingProfileTests
{
    static PricingProfileStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "pricing-profile-" + Guid.NewGuid().ToString("N"));
        return new PricingProfileStore(root);
    }

    [TestMethod]
    public void CreateEditAndDeactivateLifecycle()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.Save(new PricingProfile { Name = "Standart Marj", Formula = "x*1.4" });
            Assert.AreEqual(1, created.Version);

            var updated = store.Save(new PricingProfile { Id = created.Id, Name = "Standart Marj", Formula = "x*1.5", Version = created.Version });
            Assert.AreEqual(created.Id, updated.Id);
            Assert.AreEqual(2, updated.Version);

            var deactivated = store.Save(new PricingProfile { Id = created.Id, Name = "Standart Marj", Formula = "x*1.5", Active = false, Version = updated.Version });
            Assert.IsFalse(deactivated.Active);
            Assert.AreEqual(1, store.List().Count, "Editing must update in place, not create a second profile.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateNameIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new PricingProfile { Name = "Standart Marj", Formula = "x*1.4" });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new PricingProfile { Name = "standart marj", Formula = "x*1.5" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidFormulaIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<FormatException>(() => store.Save(new PricingProfile { Name = "Bozuk", Formula = "x + drop_table" }));
            Assert.AreEqual(0, store.List().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pricing-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            new PricingProfileStore(root).Save(new PricingProfile { Name = "Standart Marj", Formula = "x*1.4", SourceField = PricingSourceField.Price });
            var reopened = new PricingProfileStore(root).List().Single();
            Assert.AreEqual("Standart Marj", reopened.Name);
            Assert.AreEqual(PricingSourceField.Price, reopened.SourceField);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleVersionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.Save(new PricingProfile { Name = "Standart Marj", Formula = "x*1.4" });
            store.Save(new PricingProfile { Id = created.Id, Name = "Standart Marj", Formula = "x*1.5", Version = created.Version });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new PricingProfile { Id = created.Id, Name = "Standart Marj", Formula = "x*1.6", Version = created.Version }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static CatalogProduct Product() => new() { Sku = "SKU-1", Name = "Ürün", Cost = 10, Price = 25, Currency = "USD" };

    [TestMethod]
    public void CostSourceUsesCostField()
    {
        var store = NewStore(out var root);
        try
        {
            var profile = store.Save(new PricingProfile { Name = "Maliyet bazlı", Formula = "x*2", SourceField = PricingSourceField.Cost });
            Assert.AreEqual(20m, store.Evaluate(profile, Product()));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PriceSourceUsesPriceField()
    {
        var store = NewStore(out var root);
        try
        {
            var profile = store.Save(new PricingProfile { Name = "Fiyat bazlı", Formula = "x*1.1", SourceField = PricingSourceField.Price });
            Assert.AreEqual(27.5m, store.Evaluate(profile, Product()));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InactiveProfileBlocksEvaluation()
    {
        var store = NewStore(out var root);
        try
        {
            var profile = store.Save(new PricingProfile { Name = "Pasif", Formula = "x*2", Active = false });
            Assert.ThrowsException<InvalidOperationException>(() => store.Evaluate(profile, Product()));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnrecognizedSourceFieldStoredValueBlocksCalculation()
    {
        // Simulates an in-memory profile carrying a future/out-of-range enum value:
        // SourceValue's switch has no reachable default for a valid enum member,
        // only for something outside the closed set (see #2650 for the separate,
        // persisted-row version of this in PricingProfileCorruptionTests).
        var profile = new PricingProfile { Name = "X", Formula = "x", SourceField = (PricingSourceField)99 };
        Assert.ThrowsException<InvalidOperationException>(() => PricingProfileStore.SourceValue(profile, Product()));
    }
}
