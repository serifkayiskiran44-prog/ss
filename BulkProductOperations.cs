using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum BulkProductOperationKind { Activate, Deactivate, SetCategory, SetBrand, SetDescription, SetName, SetChannelMapping }
public sealed record BulkProductOperationRequest(BulkProductOperationKind Kind, string Value = "", string Channel = "", string ShopId = "", string ListingId = "", string TargetCategory = "");
public sealed class BulkProductPreviewLine
{
    public string ProductId { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";
    public string Status { get; init; } = "READY";
    public string Error { get; init; } = "";
    public DateTime ExpectedUpdatedUtc { get; init; }
    public CatalogProduct? BeforeSnapshot { get; init; }
    public CatalogProduct? AfterSnapshot { get; init; }
    public ChannelProductPlan? ChannelPlan { get; init; }
}
public sealed record BulkProductPreview(Guid Id, BulkProductOperationRequest Request, IReadOnlyList<BulkProductPreviewLine> Lines, DateTime CreatedUtc);
public sealed record BulkProductApplyResult(int Applied, int Skipped, int Errors, bool AlreadyApplied = false);

public sealed class BulkProductOperations
{
    readonly CatalogStore catalog;
    readonly ChannelProductsStore plans;
    public BulkProductOperations(CatalogStore catalog, ChannelProductsStore? plans = null) { this.catalog = catalog; this.plans = plans ?? new ChannelProductsStore(); }

    public BulkProductPreview Preview(IEnumerable<CatalogProduct> products, BulkProductOperationRequest request)
    {
        ValidateRequest(request); var rows = new List<BulkProductPreviewLine>();
        foreach (var product in products.GroupBy(x => x.Id).Select(x => x.First()))
        {
            var before = request.Kind == BulkProductOperationKind.SetChannelMapping ? plans.Find(request.Channel, request.ShopId, product.Id) : null;
            if (request.Kind == BulkProductOperationKind.SetChannelMapping)
            {
                var plan = new ChannelProductPlan { ChannelId = request.Channel.Trim().ToLowerInvariant(), ShopId = request.ShopId.Trim(), ProductId = product.Id, ListingId = request.ListingId.Trim(), TargetCategory = request.TargetCategory.Trim(), PlannedPrice = product.Price, PlannedStock = product.Stock, Currency = product.Currency, Notes = "Toplu yerel kanal planı" };
                var beforeText = before is null ? "plan yok" : $"ilan={before.ListingId}; kategori={before.TargetCategory}";
                var afterText = $"ilan={plan.ListingId}; kategori={plan.TargetCategory}";
                rows.Add(new() { ProductId = product.Id, Sku = product.Sku, Name = product.Name, Operation = $"{plan.ChannelId}/{plan.ShopId}", Before = beforeText, After = afterText, ExpectedUpdatedUtc = product.UpdatedUtc, ChannelPlan = plan }); continue;
            }
            var clone = Clone(product); var beforeValue = Value(product, request.Kind); var afterValue = beforeValue;
            if (request.Kind == BulkProductOperationKind.Activate) clone.Active = true;
            else if (request.Kind == BulkProductOperationKind.Deactivate) clone.Active = false;
            else if (request.Kind == BulkProductOperationKind.SetCategory) clone.Category = request.Value.Trim();
            else if (request.Kind == BulkProductOperationKind.SetBrand) clone.Brand = request.Value.Trim();
            else if (request.Kind == BulkProductOperationKind.SetDescription) clone.Description = request.Value;
            else if (request.Kind == BulkProductOperationKind.SetName) clone.Name = request.Value.Trim();
            afterValue = Value(clone, request.Kind);
            var status = beforeValue == afterValue ? "SKIP" : "READY"; var error = "";
            if (request.Kind == BulkProductOperationKind.SetName && string.IsNullOrWhiteSpace(clone.Name)) { status = "ERROR"; error = "Ürün adı boş olamaz."; }
            if (request.Kind == BulkProductOperationKind.SetDescription && product.LockDescription) { status = "SKIP"; error = "Açıklama kilidi etkin; toplu değişiklik uygulanmadı."; }
            rows.Add(new() { ProductId = product.Id, Sku = product.Sku, Name = product.Name, Operation = request.Kind.ToString(), Before = beforeValue, After = afterValue, Status = status, Error = error, ExpectedUpdatedUtc = product.UpdatedUtc, BeforeSnapshot = product, AfterSnapshot = clone });
        }
        return new(Guid.NewGuid(), request, rows, DateTime.UtcNow);
    }

    public BulkProductApplyResult Apply(BulkProductPreview preview, bool approved, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        if (!approved) throw new InvalidOperationException("Toplu işlem için açık onay gerekli.");
        var ready = preview.Lines.Where(x => x.Status == "READY").ToList(); var errors = preview.Lines.Count(x => x.Status == "ERROR"); var skipped = preview.Lines.Count(x => x.Status == "SKIP");
        if (ready.Count == 0) return new(0, skipped, errors);
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Request.Kind == BulkProductOperationKind.SetChannelMapping)
        {
            foreach (var line in ready) { cancellationToken.ThrowIfCancellationRequested(); var current = catalog.Products().SingleOrDefault(x => x.Id == line.ProductId) ?? throw new InvalidOperationException("Ürün silinmiş; yeni önizleme alınmalı."); if (current.UpdatedUtc != line.ExpectedUpdatedUtc) throw new InvalidOperationException($"{line.Sku} önizlemeden sonra değişti; işlem iptal edildi."); }
            var count = 0; foreach (var line in ready) { cancellationToken.ThrowIfCancellationRequested(); plans.Save(line.ChannelPlan!); count++; progress?.Report(count * 100 / ready.Count); } return new(count, skipped, errors);
        }
        var result = catalog.ApplyBulkSnapshots(ready, cancellationToken, progress); return new(result.Applied, skipped, errors);
    }

    static string Value(CatalogProduct product, BulkProductOperationKind kind) => kind switch { BulkProductOperationKind.Activate or BulkProductOperationKind.Deactivate => product.Active ? "Aktif" : "Pasif", BulkProductOperationKind.SetCategory => product.Category, BulkProductOperationKind.SetBrand => product.Brand, BulkProductOperationKind.SetDescription => product.Description, BulkProductOperationKind.SetName => product.Name, _ => "" };
    static CatalogProduct Clone(CatalogProduct product) => JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;
    static void ValidateRequest(BulkProductOperationRequest request)
    {
        if (request.Kind == BulkProductOperationKind.SetChannelMapping)
        { if (string.IsNullOrWhiteSpace(request.Channel) || string.IsNullOrWhiteSpace(request.ShopId)) throw new ArgumentException("Kanal ve mağaza zorunlu."); if (string.IsNullOrWhiteSpace(request.ListingId) && string.IsNullOrWhiteSpace(request.TargetCategory)) throw new ArgumentException("İlan ID veya hedef kategori girin."); return; }
        if ((request.Kind is BulkProductOperationKind.SetCategory or BulkProductOperationKind.SetBrand or BulkProductOperationKind.SetName or BulkProductOperationKind.SetDescription) && string.IsNullOrWhiteSpace(request.Value)) throw new ArgumentException("Yeni değer boş olamaz.");
    }
}
