namespace TrMarketplaceHubDesktop.Trendyol;

public sealed record TrendyolCatalogMatchInput(string ProductId, string Sku, string Barcode, string Gtin);
public sealed record TrendyolCatalogMatchRow(string ProductId, string Sku, string LocalCode, string RemoteBarcode, string RemoteTitle, long? CategoryId, long? BrandId, bool CanApply, string Detail);

public static class TrendyolCatalogMatch
{
    public static IReadOnlyList<TrendyolCatalogMatchRow> Preview(TrendyolWorkspaceState state, IEnumerable<TrendyolCatalogMatchInput> inputs)
    {
        var byBarcode = state.Products.GroupBy(p => Normalize(p.Barcode)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToArray());
        return inputs.Select<TrendyolCatalogMatchInput, TrendyolCatalogMatchRow>(input =>
        {
            var codes = new[] { input.Barcode, input.Gtin }.Select(code => Normalize(code)).Where(x => x.Length > 0).Distinct().ToArray();
            var candidates = codes.Where(byBarcode.ContainsKey).SelectMany(code => byBarcode[code]).DistinctBy(p => p.Barcode).ToArray();
            if (candidates.Length == 0) return new(input.ProductId, input.Sku, string.Join(" / ", codes), "", "", null, null, false, "Barkod veya GTIN Trendyol mağaza kataloğunda bulunamadı.");
            if (candidates.Length > 1) return new(input.ProductId, input.Sku, string.Join(" / ", codes), "", "", null, null, false, "Barkod ve GTIN birbiriyle çelişiyor; elle seçim gerekli.");
            var remote = candidates[0];
            if (remote.CategoryId is not > 0 || remote.BrandId is not > 0) return new(input.ProductId, input.Sku, string.Join(" / ", codes), remote.Barcode, remote.Title, remote.CategoryId, remote.BrandId, false, "Trendyol ürününde kategori veya marka ID gelmedi; ürün listesini yenileyin.");
            if (!state.Categories.Any(c => c.Id == remote.CategoryId && c.IsLeaf) || !state.Brands.Any(b => b.Id == remote.BrandId)) return new(input.ProductId, input.Sku, string.Join(" / ", codes), remote.Barcode, remote.Title, remote.CategoryId, remote.BrandId, false, "Kategori/marka sözlüğünde bulunamadı; kategori ve marka listesini yenileyin.");
            return new(input.ProductId, input.Sku, string.Join(" / ", codes), remote.Barcode, remote.Title, remote.CategoryId, remote.BrandId, true, $"Kategori {remote.CategoryId} · Marka {remote.BrandId}");
        }).ToArray();
    }

    public static IReadOnlyList<TrendyolCatalogMatchRow> Apply(TrendyolWorkspaceState state, IEnumerable<TrendyolCatalogMatchInput> inputs)
    {
        var rows = Preview(state, inputs).ToArray();
        foreach (var row in rows.Where(r => r.CanApply))
        {
            var profile = state.Profiles.SingleOrDefault(p => p.ProductId == row.ProductId);
            if (profile is null) { profile = new() { ProductId = row.ProductId }; state.Profiles.Add(profile); }
            profile.IntegrationCode = row.RemoteBarcode;
            profile.CategoryId = row.CategoryId;
            profile.BrandId = row.BrandId;
        }
        return rows;
    }

    static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
