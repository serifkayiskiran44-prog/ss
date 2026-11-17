using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Remembers live Trendyol cancel/return actions so the same action is never sent twice by a double click, a retry after a lost response, or a restart.
/// An action is recorded before the request leaves; a definite rejection by Trendyol frees it again, a timeout or crash leaves it locked as "unknown".
/// </summary>
public sealed class TrendyolActionLedger
{
    readonly string path; static readonly object Gate = new();
    public TrendyolActionLedger(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "trendyol-actions.json"); }

    public static string Key(string supplierId, string operation, string target, IEnumerable<string> parts)
    {
        var text = supplierId + "|" + operation + "|" + target + "|" + string.Join(",", parts.OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public void Begin(string key, string description)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.TryGetValue(key, out var existing)) throw new InvalidOperationException("Bu Trendyol işlemi daha önce gönderildi (" + existing + "). Sipariş/iade listesini yenileyip durumu Trendyol'da doğrulayın; tekrar göndermek çift işlem yaratabilir.");
            map[key] = description + " @ " + DateTime.UtcNow.ToString("u"); Save(map);
        }
    }
    public void Release(string key) { lock (Gate) { var map = Load(); if (map.Remove(key)) Save(map); } }

    Dictionary<string, string> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { throw new InvalidOperationException("Trendyol işlem defteri okunamadı; canlı işlem güvenlik için durduruldu."); }
    }
    void Save(Dictionary<string, string> map) { var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(map)); File.Move(tmp, path, true); }
}
