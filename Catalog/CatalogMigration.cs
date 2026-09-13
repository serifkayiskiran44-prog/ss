using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    public CatalogUndoReceipt ApplyMigration(IReadOnlyList<CatalogProduct> incoming, string sourceId)
    {
        if (incoming.Count == 0) throw new InvalidOperationException("Uygulanacak geçerli ürün yok."); if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Geçiş kaynak kimliği zorunlu.", nameof(sourceId));
        using var c = Open(); using var tx = c.BeginTransaction(); var before = Read<CatalogProduct>(c, "CatalogProducts", tx).Select(x => JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(x))!).ToList(); var products = Read<CatalogProduct>(c, "CatalogProducts", tx); var touched = new HashSet<string>();
        foreach (var input in incoming)
        {
            input.Name = TitleNormalizer.Normalize(input.Name).Normalized; /* #905 */ Valid(input); var bySku = input.Sku.Length == 0 ? null : products.SingleOrDefault(x => x.Sku.Equals(input.Sku, StringComparison.OrdinalIgnoreCase)); var byBarcode = input.Barcode.Length == 0 ? null : products.SingleOrDefault(x => x.Barcode.Equals(input.Barcode, StringComparison.OrdinalIgnoreCase)); if (bySku is not null && byBarcode is not null && bySku.Id != byBarcode.Id) throw new InvalidOperationException("SKU ve barkod farklı ürünlerle eşleşiyor."); var old = bySku ?? byBarcode; if (old is not null && !touched.Add(old.Id)) throw new InvalidOperationException("Dosyada aynı ürün birden fazla satırda güncelleniyor.");
            if (old is null) { var created = JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(input))!; created.SourceId = sourceId; created.Id = Guid.NewGuid().ToString("N"); created.UpdatedUtc = DateTime.UtcNow; products.Add(created); Put(c, "CatalogProducts", created.Id, created, tx); continue; }
            if (!old.LockName) old.Name = input.Name; if (!old.LockDescription) old.Description = input.Description; if (!old.LockPrice) { old.Cost = input.Cost; old.Price = input.Price; old.Currency = input.Currency; old.CostCurrency = input.CostCurrency; } else old.Cost = input.Cost; if (!old.LockStock) old.Stock = input.Stock; if (!old.LockImages) old.ImageUrls = input.ImageUrls; old.Brand = input.Brand; old.Category = input.Category; old.Gtin = input.Gtin; old.VatRate = input.VatRate; old.Active = input.Active; old.UpdatedUtc = DateTime.UtcNow; Put(c, "CatalogProducts", old.Id, old, tx);
        }
        tx.Commit(); using var afterConnection = Open(); var after = Read<CatalogProduct>(afterConnection, "CatalogProducts"); return new CatalogUndoReceipt(Guid.NewGuid().ToString("N"), before, after);
    }
}
