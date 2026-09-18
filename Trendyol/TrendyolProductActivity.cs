using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed record TrendyolProductActivity(string CreateMessage, string UpdateMessage);

public sealed partial class TrendyolWorkspaceStore
{
    public IReadOnlyDictionary<string, TrendyolProductActivity> ProductActivities(string seller)
    {
        Seller(seller);
        using var connection = Open();
        using var command = connection.CreateCommand();
        // One read statement gives the receipt/immutable-plan join a consistent snapshot.
        // Limit receipts before joining so another seller or an unsent preview cannot
        // displace the selected seller's recent activity.
        command.CommandText = """
            SELECT recent.PlanId,recent.CreatedUtc,recent.Json,plan.Json
            FROM (
                SELECT PlanId,SellerId,CreatedUtc,Json FROM TrendyolReceipts
                WHERE SellerId=$seller ORDER BY CreatedUtc DESC,PlanId DESC LIMIT 300
            ) AS recent
            LEFT JOIN TrendyolPlans AS plan ON plan.Id=recent.PlanId AND plan.SellerId=recent.SellerId
            ORDER BY recent.CreatedUtc DESC,recent.PlanId DESC
            """;
        command.Parameters.AddWithValue("$seller", seller);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, TrendyolProductActivity>(StringComparer.Ordinal);
        try
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(3)) throw InvalidActivity();
                var id = reader.GetString(0);
                var receipt = JsonSerializer.Deserialize<TrendyolReceipt>(reader.GetString(2)) ?? throw InvalidActivity();
                var plan = JsonSerializer.Deserialize<TrendyolPlan>(reader.GetString(3)) ?? throw InvalidActivity();
                if (receipt.PlanId != id || plan.Id != id || receipt.SellerId != seller || plan.SellerId != seller
                    || !Enum.IsDefined(plan.Operation) || receipt.Operation != plan.Operation.ToString()
                    || plan.Rows is null || plan.Rows.Any(row => row is null || string.IsNullOrWhiteSpace(row.ProductId))
                    || string.IsNullOrWhiteSpace(receipt.Status)
                    || !DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var storedCreated)
                    || storedCreated.ToUniversalTime() != receipt.CreatedUtc.ToUniversalTime()) throw InvalidActivity();

                var sentProducts = plan.Rows.Where(row => row.ItemJson is not null).Select(row => row.ProductId).Distinct(StringComparer.Ordinal);
                var message = ActivityMessage(receipt, plan.Rows.Count > 1);
                foreach (var productId in sentProducts)
                {
                    var current = result.GetValueOrDefault(productId) ?? new TrendyolProductActivity("", "");
                    // Receipts arrive newest first; create and update have independent slots.
                    if (plan.Operation == TrendyolOperation.Create && current.CreateMessage.Length == 0)
                        current = current with { CreateMessage = message };
                    else if (plan.Operation != TrendyolOperation.Create && current.UpdateMessage.Length == 0)
                        current = current with { UpdateMessage = message };
                    result[productId] = current;
                }
            }
        }
        catch (JsonException) { throw InvalidActivity(); }
        return new ReadOnlyDictionary<string, TrendyolProductActivity>(result);
    }

    static string ActivityMessage(TrendyolReceipt receipt, bool multipleProducts)
    {
        var date = receipt.CreatedUtc.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var status = MarketplaceConnectionStore.Redact(receipt.Status);
        if (multipleProducts) return $"Toplu gönderim: {status} · {date} — Ayrıntılar işlem geçmişinde.";
        var detail = string.IsNullOrWhiteSpace(receipt.Detail) ? "" : " — " + MarketplaceConnectionStore.Redact(receipt.Detail);
        return $"{status} · {date}{detail}";
    }

    static InvalidDataException InvalidActivity() => new("Trendyol ürün gönderim geçmişi veya hesap kimliği tutarsız.");
}
