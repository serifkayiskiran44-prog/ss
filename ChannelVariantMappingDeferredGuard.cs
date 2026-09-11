namespace TrMarketplaceHubDesktop;

public sealed record ChannelVariantMappingDecision(string Status, string Detail, string Channel);

public static class ChannelVariantMappingDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";

    public static ChannelVariantMappingDecision Evaluate(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Kanal kimliği zorunlu.", nameof(channel));
        return new(Status, "Marketplace seçenek/varyant eşleme kullanıcı tarafından ertelendi; connector publish preview/write başlatılmadı.", channel.Trim());
    }

    public static void EnsureNoPublish(ChannelVariantMappingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: kanal varyant eşleme kapsam dışı; publish başlatılmadı.");
    }
}
