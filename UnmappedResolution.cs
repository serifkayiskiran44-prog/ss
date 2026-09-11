using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record UnmappedRecord(string Source, string Channel, string ShopId, string ExternalId, string Sku, string Barcode, string Name, string Brand);
public sealed record MappingSuggestion(string Fingerprint, string ProductId, string MatchKind, string ShopId, string Detail);

public static class UnmappedResolution
{
    public static IReadOnlyList<MappingSuggestion> Suggest(UnmappedRecord record, IEnumerable<Catalog.CatalogProduct> products)
    {
        ArgumentNullException.ThrowIfNull(record); ArgumentNullException.ThrowIfNull(products);
        var candidates = products.Where(p => (!string.IsNullOrWhiteSpace(record.Sku) && p.Sku.Equals(record.Sku, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(record.Barcode) && p.Barcode.Equals(record.Barcode, StringComparison.OrdinalIgnoreCase)) || Normalize(p.Name) == Normalize(record.Name) && Normalize(p.Brand) == Normalize(record.Brand)).ToArray();
        var kind = candidates.Length == 1 ? "SUGGESTED" : candidates.Length > 1 ? "AMBIGUOUS" : "NO_MATCH";
        return candidates.Select(p => new MappingSuggestion(Fingerprint(record), p.Id, kind, record.ShopId, kind == "SUGGESTED" ? "Kullanıcı onayı bekleniyor; otomatik eşleme yapılmadı." : "Birden fazla aday var; manuel çözüm gerekli.")).ToArray();
    }

    public static void EnsureApproved(MappingSuggestion suggestion, string channel, string shopId)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.MatchKind != "SUGGESTED" || !suggestion.ShopId.Equals(shopId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(channel)) throw new InvalidOperationException("Eşleme önerisi onaylanabilir değil; kanal/mağaza veya aday durumunu kontrol edin.");
    }

    public static string Fingerprint(UnmappedRecord record) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", record.Source.Trim(), record.Channel.Trim().ToLowerInvariant(), record.ShopId.Trim(), record.ExternalId.Trim(), record.Sku.Trim(), record.Barcode.Trim()))));
    static string Normalize(string value) => string.Join(' ', new string((value ?? "").Trim().ToUpperInvariant().Normalize().Where(c => !char.IsPunctuation(c)).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
