using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class ProductSourceBindingTests
{
    static (CatalogStore Catalog, ProductSourceBindingStore Bindings, string Directory) Open(string? directory = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), "source-binding-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(directory);
        return (catalog, new ProductSourceBindingStore(directory, catalog), directory);
    }

    static XmlSource AddSource(CatalogStore catalog, string id, bool enabled = true)
    {
        var source = new XmlSource { Id = id, Name = id, Location = "https://example.test/" + id + ".xml", Enabled = enabled };
        catalog.SaveSource(source);
        return source;
    }

    [TestMethod]
    public void OneProductCanTakeContentPriceAndStockFromDifferentSources()
    {
        var (catalog, bindings, _) = Open();
        AddSource(catalog, "xml-a"); AddSource(catalog, "xml-b");
        var product = catalog.CreateManual(new() { Sku = "P1", Name = "Food", Barcode = "8690000000012" });

        bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "xml-a", true, 0, default), 0);
        bindings.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-b", true, 0, default), 0);

        var rows = bindings.Get(product.Id).ToDictionary(r => r.Group);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("xml-a", rows[ProductFieldGroup.Content].SourceId);
        Assert.AreEqual("xml-b", rows[ProductFieldGroup.Price].SourceId);
        Assert.AreEqual(ProductSourceKind.Manual, rows[ProductFieldGroup.OnlineStock].Kind);   // untouched group stays manual
        Assert.AreEqual(1, rows[ProductFieldGroup.Content].Version);
        Assert.AreEqual(0, rows[ProductFieldGroup.OnlineStock].Version);
    }

    [TestMethod]
    public void ChangingASourceNeverChangesTheProductOrCreatesASalesBinding()
    {
        var (catalog, bindings, _) = Open();
        AddSource(catalog, "hepsiburada-labelled-feed");
        var product = catalog.CreateManual(new() { Sku = "P1", Name = "Food", Barcode = "8690000000012", Stock = 7, Price = 10m });
        var before = System.Text.Json.JsonSerializer.Serialize(catalog.Products().Single());

        bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "hepsiburada-labelled-feed", true, 0, default), 0);

        Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(catalog.Products().Single()));
        Assert.AreEqual(3, bindings.Get(product.Id).Count);   // only the three field groups exist: no per-shop rows
    }

    [TestMethod]
    public void StaleVersionUnknownProductAndBadSourcesAreRejected()
    {
        var (catalog, bindings, _) = Open();
        AddSource(catalog, "xml-a"); AddSource(catalog, "off", enabled: false);
        var product = catalog.CreateManual(new() { Sku = "P1", Name = "Food" });
        bindings.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-a", true, 0, default), 0);

        Assert.ThrowsException<InvalidOperationException>(() =>        // second writer still holds version 0
            bindings.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Manual, "", true, 0, default), 0));
        Assert.ThrowsException<InvalidOperationException>(() =>
            bindings.Save(new("missing", ProductFieldGroup.Price, ProductSourceKind.Manual, "", true, 0, default), 0));
        Assert.ThrowsException<InvalidOperationException>(() =>
            bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "nope", true, 0, default), 0));
        Assert.ThrowsException<InvalidOperationException>(() =>
            bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "off", true, 0, default), 0));
        Assert.ThrowsException<InvalidOperationException>(() =>        // a manual binding must not name a source
            bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Manual, "xml-a", true, 0, default), 0));
        Assert.AreEqual(1, bindings.Get(product.Id).Single(r => r.Group == ProductFieldGroup.Price).Version);
    }

    [TestMethod]
    public void RefreshPreviewSkipsBindingsWhoseSourceWasDeletedOrDisabledLater()
    {
        var (catalog, bindings, _) = Open();
        var source = AddSource(catalog, "xml-a");
        var product = catalog.CreateManual(new() { Sku = "P1", Name = "Food" });
        var saved = bindings.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-a", true, 0, default), 0);
        Assert.IsTrue(bindings.IsUsable(saved));

        source.Enabled = false; catalog.SaveSource(source);
        Assert.IsFalse(bindings.IsUsable(saved));
        Assert.IsTrue(bindings.IsUsable(bindings.Get(product.Id).Single(r => r.Group == ProductFieldGroup.Content)));   // manual is always usable
        Assert.IsFalse(bindings.IsUsable(saved with { Enabled = false }));
    }

    [TestMethod]
    public void MigrationTranslatesRecordedSourcesWithoutRewritingTheCatalogAndIsIdempotent()
    {
        var (catalog, bindings, directory) = Open();
        var feed = AddSource(catalog, "xml-a");
        var imported = new CatalogProduct { SourceId = feed.Id, SourceKind = "xml", PriceSource = "xml", StockSource = "xml", Sku = "X1", Name = "Imported", Barcode = "8690000000029", Stock = 4, Price = 5m };
        catalog.Import(feed, [imported]);
        var manual = catalog.CreateManual(new() { Sku = "M1", Name = "Manual" });
        var orphan = catalog.CreateManual(new() { Sku = "O1", Name = "Orphan", SourceKind = "xml", SourceId = "deleted-feed", PriceSource = "xml" });
        var catalogBefore = System.Text.Json.JsonSerializer.Serialize(catalog.Products().OrderBy(p => p.Sku));

        Assert.IsTrue(bindings.MigrateFromCatalog() > 0);
        Assert.AreEqual(catalogBefore, System.Text.Json.JsonSerializer.Serialize(catalog.Products().OrderBy(p => p.Sku)));

        var xProduct = catalog.Products().Single(p => p.Sku == "X1");
        var x = bindings.Get(xProduct.Id).ToDictionary(r => r.Group);
        Assert.AreEqual("xml-a", x[ProductFieldGroup.Content].SourceId);
        Assert.AreEqual("xml-a", x[ProductFieldGroup.Price].SourceId);
        Assert.AreEqual("xml-a", x[ProductFieldGroup.OnlineStock].SourceId);
        Assert.IsTrue(bindings.Get(manual.Id).All(r => r.Kind == ProductSourceKind.Manual));
        Assert.IsTrue(bindings.Get(orphan.Id).All(r => r.Kind == ProductSourceKind.Manual));   // unknown source: no dangling reference

        var user = bindings.Save(new(xProduct.Id, ProductFieldGroup.Price, ProductSourceKind.Manual, "", true, 0, default), x[ProductFieldGroup.Price].Version);
        Assert.AreEqual(0, new ProductSourceBindingStore(directory, catalog).MigrateFromCatalog());   // second run changes nothing
        Assert.AreEqual(ProductSourceKind.Manual, bindings.Get(xProduct.Id).Single(r => r.Group == ProductFieldGroup.Price).Kind);
        Assert.AreEqual(user.Version, bindings.Get(xProduct.Id).Single(r => r.Group == ProductFieldGroup.Price).Version);
    }

    [TestMethod]
    public void BindingsSurviveReopenAndDeletedProductsLoseTheirRows()
    {
        var (catalog, bindings, directory) = Open();
        AddSource(catalog, "xml-a");
        var product = catalog.CreateManual(new() { Sku = "P1", Name = "Food" });
        bindings.Save(new(product.Id, ProductFieldGroup.OnlineStock, ProductSourceKind.Xml, "xml-a", true, 0, default), 0);

        var reopened = new ProductSourceBindingStore(directory, new CatalogStore(directory));
        Assert.AreEqual("xml-a", reopened.Get(product.Id).Single(r => r.Group == ProductFieldGroup.OnlineStock).SourceId);

        catalog.DeleteProduct(product);
        Assert.AreEqual(1, reopened.RemoveOrphans());          // the stored row of the deleted product goes
        Assert.AreEqual(0, reopened.RemoveOrphans());
        Assert.IsTrue(reopened.Get(product.Id).All(r => r.Version == 0 && r.Kind == ProductSourceKind.Manual));
    }
}
