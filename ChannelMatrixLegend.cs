namespace TrMarketplaceHubDesktop;

/// <summary>One state of the matrix as the legend explains it: the glyph and word a cell shows, and what they mean.</summary>
public sealed record ChannelMatrixLegendEntry(string Key, string Glyph, string Word, string Description, SeverityLevel Level)
{
    public string Badge => $"{Glyph} {Word}";
}

/// <summary>
/// The shared legend of the channel matrix (#843): the one table that says what every cell marker means, used by
/// the cells themselves so a cell and its legend can never disagree. The entries are exactly the states the
/// listing service records -- synced, pending, draft, stale, missing, error -- plus the two facts that outrank
/// them: a store connection that is not usable, and a channel this build can only plan for locally (no live
/// capability), which is a real capability fact from the connection catalogue, not a guess. Glyph and word carry
/// the meaning; colour only echoes it, so the legend reads the same under high contrast.
/// </summary>
public static class ChannelMatrixLegend
{
    public const string AuthError = "AUTH_ERROR";
    public const string LocalOnly = "LOCAL_ONLY";
    public const string None = "NONE";
    public const string CategoryStale = "CATEGORY_STALE"; // #914

    public static readonly IReadOnlyList<ChannelMatrixLegendEntry> Entries = new[]
    {
        new ChannelMatrixLegendEntry("SYNCED", "✔", "yayında", "İlan kimliği var ve son senkron başarılı.", SeverityLevel.Success),
        new ChannelMatrixLegendEntry("PENDING", "⏳", "bekliyor", "Senkron kuyrukta veya sürüyor.", SeverityLevel.Info),
        new ChannelMatrixLegendEntry("DRAFT", "✎", "taslak", "Yerel plan ve ilan kimliği var; henüz senkron kaydı yok.", SeverityLevel.Info),
        new ChannelMatrixLegendEntry("STALE", "◔", "bayat", "Yerel plan 180 günden eski; yeniden gözden geçirin.", SeverityLevel.Warning),
        new ChannelMatrixLegendEntry("MISSING", "○", "eşleme yok", "Bu mağaza için ilan kimliği/eşleme kaydı yok.", SeverityLevel.Warning),
        new ChannelMatrixLegendEntry("ERROR", "✖", "hata", "Son senkron başarısız; hata Liste görünümünde.", SeverityLevel.Blocking),
        new ChannelMatrixLegendEntry(CategoryStale, "⚑", "kategori", "Ürünün kategorisi bu mağazada pasif; ilan gönderimi kategori düzelmeden engellenir.", SeverityLevel.Blocking), // #914
        new ChannelMatrixLegendEntry(AuthError, "⚠", "bağlantı", "Mağaza bağlantısı başarısız, engelli veya yapılandırılmamış; hücre durumu bu düzelmeden anlamsız.", SeverityLevel.Blocking),
        new ChannelMatrixLegendEntry(LocalOnly, "⊘", "yalnız yerel", "Bu kanal için bu sürümde canlı ilan yeteneği yok; yalnızca yerel plan tutulur.", SeverityLevel.Info),
        new ChannelMatrixLegendEntry(None, "·", "kayıt yok", "Ürünün bu mağaza için hiç kaydı yok.", SeverityLevel.Info),
    };

    static readonly IReadOnlyDictionary<string, ChannelMatrixLegendEntry> ByKey = Entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The mapping statuses the listing service can record -- the legend must explain each of them.</summary>
    public static readonly IReadOnlyList<string> ServiceStatuses = new[] { "MISSING", "STALE", "ERROR", "PENDING", "SYNCED", "DRAFT", CategoryStale };

    /// <summary>Which entry a cell shows: a channel without live capability first (its connection status is meaningless -- there is nothing to connect to), then connection trouble, then the recorded mapping state; an unknown state falls back to "kayıt yok".</summary>
    public static ChannelMatrixLegendEntry For(string? mappingStatus, string? authStatus, bool localOnly)
    {
        if (localOnly) return ByKey[LocalOnly];
        if (string.Equals(authStatus, AuthError, StringComparison.OrdinalIgnoreCase)) return ByKey[AuthError];
        return ByKey.TryGetValue((mappingStatus ?? "").Trim(), out var entry) && entry.Key != AuthError && entry.Key != LocalOnly ? entry : ByKey[None];
    }

    public static ChannelMatrixLegendEntry Get(string key) => ByKey.TryGetValue(key, out var entry) ? entry : ByKey[None];

    /// <summary>The states present in a matrix with their counts, in legend order -- nothing that is not on screen.</summary>
    public static IReadOnlyList<(ChannelMatrixLegendEntry Entry, int Count)> Counts(ChannelMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in matrix.Rows) foreach (var cell in row.Cells) { var key = (cell ?? ChannelMatrixRow.Empty).Legend.Key; counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1; }
        return Entries.Where(e => counts.ContainsKey(e.Key)).Select(e => (e, counts[e.Key])).ToList();
    }
}
