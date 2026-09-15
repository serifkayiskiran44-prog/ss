using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record StockPolicyPreview(string Channel, string Shop, string ProductId, string Sku, int Stock, int AvailableStock, int PolicyVersion, DateTime ProductUpdatedUtc, DateTime PolicyUpdatedUtc);
public sealed record PricePolicyPreview(string Channel, string Shop, string ProductId, string Sku, decimal Price, string Currency, decimal FormulaPriceTry, decimal CostTry, int PolicyVersion, DateTime ProductUpdatedUtc, DateTime PolicyUpdatedUtc);
/// Bounded diagnostics only (Channel/Shop identity, a short reason code,
/// detection time) - never Formula or other policy field content - for a
/// StockPolicies/PricePolicies row that failed to deserialize or re-validate.
/// See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptPolicyRow(string Channel, string Shop, string Reason, DateTime DetectedUtc);

public partial class CatalogStore
{
    const int MaxPolicyJsonBytes = 200_000;
    /// Never conflated with a DB-busy/locked SqliteException, which is raised by
    /// the surrounding command, not this parse/validation.
    static bool TryReadStockPolicyRow(string channel, string shop, string json, out StockPolicy? policy, out CorruptPolicyRow? corrupt)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(json)) { corrupt = new(channel, shop, "Boş kayıt", DateTime.UtcNow); return false; }
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPolicyJsonBytes) { corrupt = new(channel, shop, "Kayıt boyutu sınırı aşıyor", DateTime.UtcNow); return false; }
        StockPolicy? candidate;
        try { candidate = JsonSerializer.Deserialize<StockPolicy>(json); }
        catch (JsonException) { corrupt = new(channel, shop, "Geçersiz JSON", DateTime.UtcNow); return false; }
        if (candidate is null) { corrupt = new(channel, shop, "Boş JSON", DateTime.UtcNow); return false; }
        if (!string.Equals(candidate.Channel, channel, StringComparison.Ordinal) || !string.Equals(candidate.Shop, shop, StringComparison.Ordinal)) { corrupt = new(channel, shop, "Kimlik uyuşmazlığı (yanlış kanal/mağaza)", DateTime.UtcNow); return false; }
        if (candidate.SafetyStock < 0 || candidate.MaximumStock < 0) { corrupt = new(channel, shop, "Negatif stok değeri", DateTime.UtcNow); return false; }
        policy = candidate; corrupt = null; return true;
    }
    static bool TryReadPricePolicyRow(string channel, string shop, string json, out PricePolicy? policy, out CorruptPolicyRow? corrupt)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(json)) { corrupt = new(channel, shop, "Boş kayıt", DateTime.UtcNow); return false; }
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPolicyJsonBytes) { corrupt = new(channel, shop, "Kayıt boyutu sınırı aşıyor", DateTime.UtcNow); return false; }
        PricePolicy? candidate;
        try { candidate = JsonSerializer.Deserialize<PricePolicy>(json); }
        catch (JsonException) { corrupt = new(channel, shop, "Geçersiz JSON", DateTime.UtcNow); return false; }
        if (candidate is null) { corrupt = new(channel, shop, "Boş JSON", DateTime.UtcNow); return false; }
        if (!string.Equals(candidate.Channel, channel, StringComparison.Ordinal) || !string.Equals(candidate.Shop, shop, StringComparison.Ordinal)) { corrupt = new(channel, shop, "Kimlik uyuşmazlığı (yanlış kanal/mağaza)", DateTime.UtcNow); return false; }
        if (candidate.Formula.Length is 0 or > 8192 || candidate.Currency.Length != 3 || candidate.TryPerUnit <= 0 || candidate.MinimumPrice < 0 || candidate.MinimumMarginTry < 0) { corrupt = new(channel, shop, "Geçersiz fiyat kuralı alanı", DateTime.UtcNow); return false; }
        policy = candidate; corrupt = null; return true;
    }
    /// A malformed/legacy-invalid row must never crash the whole policy list -
    /// it's excluded from the healthy result and reported only via
    /// CorruptStockPolicies()/CorruptPricePolicies(); it is never auto-deleted,
    /// auto-repaired, or silently normalized to a default policy.
    public IReadOnlyList<StockPolicy> ListStockPolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,Shop,Json FROM StockPolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<StockPolicy>();
        while (reader.Read()) if (TryReadStockPolicyRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), out var policy, out _)) rows.Add(policy!);
        return rows;
    }
    public IReadOnlyList<PricePolicy> ListPricePolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,Shop,Json FROM PricePolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<PricePolicy>();
        while (reader.Read()) if (TryReadPricePolicyRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), out var policy, out _)) rows.Add(policy!);
        return rows;
    }
    /// Read-only diagnostics: Channel/Shop identity + bounded reason code only,
    /// never raw Formula/other policy content.
    public IReadOnlyList<CorruptPolicyRow> CorruptStockPolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,Shop,Json FROM StockPolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<CorruptPolicyRow>();
        while (reader.Read()) if (!TryReadStockPolicyRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), out _, out var corrupt)) rows.Add(corrupt!);
        return rows;
    }
    public IReadOnlyList<CorruptPolicyRow> CorruptPricePolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,Shop,Json FROM PricePolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<CorruptPolicyRow>();
        while (reader.Read()) if (!TryReadPricePolicyRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), out _, out var corrupt)) rows.Add(corrupt!);
        return rows;
    }
    public StockPolicy CopyStockPolicy(string sourceChannel, string sourceShop, string targetChannel, string targetShop)
    {
        var source = GetStockPolicy(sourceChannel, sourceShop) ?? throw new InvalidOperationException("Kaynak stok politikası bulunamadı."); var target = GetStockPolicy(targetChannel, targetShop); return SaveStockPolicy(new() { Channel = targetChannel, Shop = targetShop, SafetyStock = source.SafetyStock, MaximumStock = source.MaximumStock, Enabled = source.Enabled, Version = target?.Version ?? 0 });
    }
    public PricePolicy CopyPricePolicy(string sourceChannel, string sourceShop, string targetChannel, string targetShop)
    {
        var source = GetPricePolicy(sourceChannel, sourceShop) ?? throw new InvalidOperationException("Kaynak fiyat politikası bulunamadı."); var target = GetPricePolicy(targetChannel, targetShop); return SavePricePolicy(new() { Channel = targetChannel, Shop = targetShop, Formula = source.Formula, Currency = source.Currency, TryPerUnit = source.TryPerUnit, MinimumPrice = source.MinimumPrice, MinimumMarginTry = source.MinimumMarginTry, Enabled = source.Enabled, Version = target?.Version ?? 0 });
    }
    public StockPolicyPreview PreviewStockDetailed(string channel, string shop, string productId)
    {
        var policy = GetStockPolicy(channel, shop) ?? throw new InvalidOperationException("Önce mağaza stok politikasını kaydedin."); if (!policy.Enabled) throw new InvalidOperationException("Stok politikası pasif."); var product = Products().SingleOrDefault(x => x.Id == productId) ?? throw new InvalidOperationException("Ürün bulunamadı."); var available = product.Active ? Math.Max(0, product.Stock - policy.SafetyStock) : 0; if (policy.MaximumStock.HasValue) available = Math.Min(available, policy.MaximumStock.Value); return new(policy.Channel, policy.Shop, product.Id, product.Sku, product.Stock, available, policy.Version, product.UpdatedUtc, policy.UpdatedUtc);
    }
    public PricePolicyPreview PreviewPriceDetailed(string channel, string shop, string productId)
    {
        var policy = GetPricePolicy(channel, shop) ?? throw new InvalidOperationException("Önce mağaza fiyat politikasını kaydedin."); if (!policy.Enabled) throw new InvalidOperationException("Fiyat politikası pasif."); var product = Products().SingleOrDefault(x => x.Id == productId) ?? throw new InvalidOperationException("Ürün bulunamadı."); var formula = PriceFormula.Evaluate(policy.Formula, product.Cost); if (formula <= 0) throw new InvalidOperationException("Formül pozitif fiyat üretmedi."); var rate = policy.Currency == "TRY" ? 1 : policy.TryPerUnit; var price = Math.Round(formula / rate, 2, MidpointRounding.AwayFromZero); if (price < policy.MinimumPrice || formula - product.Cost < policy.MinimumMarginTry) throw new InvalidOperationException("Fiyat koruması kuralı reddetti."); return new(policy.Channel, policy.Shop, product.Id, product.Sku, price, policy.Currency, formula, product.Cost, policy.Version, product.UpdatedUtc, policy.UpdatedUtc);
    }
}
