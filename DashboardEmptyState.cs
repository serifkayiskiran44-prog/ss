namespace TrMarketplaceHubDesktop;

/// <summary>What the board knows about itself when it has nothing to show.</summary>
public sealed record DashboardEmptyInput
{
    public int Connections { get; init; }
    public int Sources { get; init; }
    public int SourcesEverRun { get; init; }
    public int SourcesWithSuccessfulFeed { get; init; }
    public int Products { get; init; }
    public int Orders { get; init; }
    public bool Filtered { get; init; }
    public int VisibleRecords { get; init; }
    public string ScopeLabel { get; init; } = "tüm mağazalar";
}

public sealed record DashboardEmptyStateView(string Reason, string Title, string Detail, string ActionLabel, string Route, bool HasAction, bool IsEmpty);

/// <summary>
/// The dashboard's zero-data state (#811). "Veri yok" is the same sentence for five different situations and
/// helps in none of them, so this separates them and gives each one step: no store connected, no feed defined,
/// a feed that has never run, a feed that has run but never brought anything (which is a connection problem,
/// not an unstarted one), a filter hiding the data, and a working setup that simply produced no rows. The
/// ladder answers the *first* thing missing, because telling someone their filter is too narrow while they have
/// no store connected is worse than saying nothing. Every step points at a screen this build actually has --
/// the caller supplies that check, so a missing screen drops the button instead of promising a capability the
/// app does not have; nothing here names an endpoint, a token or a marketplace operation.
/// </summary>
public static class DashboardEmptyState
{
    public const string NoStore = "NO_STORE";
    public const string NoSource = "NO_SOURCE";
    public const string NeverRun = "NEVER_RUN";
    public const string DisconnectedSource = "DISCONNECTED_SOURCE";
    public const string FilteredEmpty = "FILTERED_EMPTY";
    public const string NoRecords = "NO_RECORDS";
    public const string None = "NONE";

    public static DashboardEmptyStateView Evaluate(DashboardEmptyInput input, Func<string, bool> routeExists)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(routeExists);
        var scope = string.IsNullOrWhiteSpace(input.ScopeLabel) ? "tüm mağazalar" : input.ScopeLabel.Trim();

        if (input.Connections <= 0)
            return Build(NoStore, "Henüz bağlı mağaza yok",
                "Pano, bağlı mağazalardan okur. Önce bir mağaza bağlantısı ekleyip salt okunur testini çalıştırın.",
                "Mağaza bağlantılarını aç", "connections", routeExists);

        if (input.Sources <= 0)
            return Build(NoSource, "Henüz ürün kaynağı tanımlı değil",
                "Mağaza bağlı ama ürünlerin geleceği bir XML kaynağı yok. Bir kaynak tanımlayıp önizleyin.",
                "XML kaynaklarını aç", "xml", routeExists);

        if (input.SourcesEverRun <= 0)
            return Build(NeverRun, "Kaynak tanımlı ama hiç çalışmadı",
                "Kaynak duruyor, henüz bir kez bile okunmadı. Önizleyip ilk içe aktarmayı yapın; önizleme olmadan kayıt yazılmaz.",
                "Kaynağı önizle", "xml", routeExists);

        if (input.SourcesWithSuccessfulFeed <= 0)
            return Build(DisconnectedSource, "Kaynak çalıştı ama hiç veri getirmedi",
                $"Kaynak en az bir kez denendi, başarılı bir besleme hiç alınamadı: kaynağın adresine ve kullanıcı bilgilerine bağlanılamıyor olabilir. Kaynak kaydını kontrol edip yeniden bağlayın. Kapsam: {scope}.",
                "Kaynak kaydını aç", "xml", routeExists);

        if (input.Filtered && input.VisibleRecords <= 0)
            return Build(FilteredEmpty, "Bu kapsamda gösterilecek kayıt yok",
                $"Veri var ama seçili kapsam ({scope}) hiçbir kayıt eşleştirmiyor. Kapsamı tüm mağazalara döndürüp yeniden bakın.",
                "Tüm mağazalara dön", "dashboard", routeExists);

        if (input.Products <= 0 && input.Orders <= 0)
            return Build(NoRecords, "Kurulum tamam, kayıt yok",
                "Mağaza, kaynak ve besleme yerinde ama ne ürün ne sipariş var. Kaynağı yeniden önizleyip içe aktarmayı tamamlayın.",
                "Ürünleri aç", "products", routeExists);

        return new(None, "", "", "", "", false, false);
    }

    static DashboardEmptyStateView Build(string reason, string title, string detail, string actionLabel, string route, Func<string, bool> routeExists)
    {
        // A button that cannot open anything is worse than no button: keep the diagnosis, drop the promise.
        var supported = routeExists(route);
        return new(reason, title, detail, supported ? actionLabel : "", supported ? route : "", supported, true);
    }
}
