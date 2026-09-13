using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record EtsyReadinessCheck(string Key, string Status, string Detail);

public sealed record EtsyReadinessReport(DateTime AtUtc, IReadOnlyList<EtsyReadinessCheck> Checks)
{
    public bool Ready => OverallStatus == "SATIŞA HAZIR";
    public string OverallStatus => Checks.Any(x => x.Status is "BLOCKED" or "ERROR") ? "EKSİK / LIVE_API_BLOCKED" : Checks.Any(x => x.Status == "WARN") ? "EKSİK" : "SATIŞA HAZIR";
}

public sealed class EtsyReadinessService
{
    readonly string directory;
    public EtsyReadinessService(string? dataDirectory = null) => directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");

    public EtsyReadinessReport Build()
    {
        var checks = new List<EtsyReadinessCheck>();
        var credentials = CredentialStore.Load(directory);
        checks.Add(credentials is { Key.Length: > 0, Secret.Length: > 0, Token.Length: > 0, RefreshToken.Length: > 0 } && long.TryParse(credentials.ShopId, out var shop) && shop > 0
            ? new("credentials", credentials.IsAccessTokenUsable() ? "PASS" : "BLOCKED", credentials.IsAccessTokenUsable() ? $"Mağaza {shop} için şifreli Etsy credential kaydı bulundu." : "Etsy erişim belirteci süresi dolmuş veya dolmak üzere; refresh gerekli.")
            : new("credentials", "BLOCKED", "Etsy Key/Secret/Token/RefreshToken/Shop ID eksik; canlı işlem engellendi."));

        var template = TemplateStore.Load(directory);
        var templateOk = template.TaxonomyId > 0 && template.ShippingProfileId > 0 && template.ReadinessStateId > 0 && !string.IsNullOrWhiteSpace(template.Currency);
        checks.Add(templateOk
            ? new("listing-template", "PASS", "Kategori, kargo profili, hazırlık profili ve mağaza para birimi tanımlı.")
            : new("listing-template", "BLOCKED", "Etsy ilan şablonunda kategori, kargo profili, hazırlık profili veya para birimi eksik."));

        try
        {
            var products = new Catalog.CatalogStore(directory).Products();
            var mapped = products.Count(x => !string.IsNullOrWhiteSpace(x.EtsyListingId));
            var unmapped = products.Count - mapped;
            checks.Add(products.Count == 0 ? new("catalog", "WARN", "Ana ürün havuzunda dry-run yapılacak ürün yok.") : new("catalog", "PASS", $"{products.Count:N0} ürün bulundu; {mapped:N0} Etsy ilanı eşleşmiş, {unmapped:N0} yerel taslak bekliyor."));
            var candidate = products.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.EtsyListingId));
            if (candidate is null) checks.Add(new("dry-run", "WARN", "Eşleşmemiş ürün bulunamadı; canlı gönderim simülasyonu çalıştırılmadı."));
            else
            {
                var errors = EtsyDrafts.Validate(candidate, template);
                checks.Add(errors.Count == 0
                    ? new("dry-run", "PASS", $"{candidate.Sku} için yerel ilan dry-run kontrolleri geçti; HTTP yazma çağrısı yapılmadı.")
                    : new("dry-run", "BLOCKED", $"{candidate.Sku} dry-run doğrulaması başarısız: {string.Join(" · ", errors)}"));
            }
        }
        catch (Exception error) { checks.Add(new("catalog", "ERROR", AuditStore.Sanitize(error.Message))); }

        checks.Add(new("live-write-gate", "PASS", "Onaylı preview olmadan canlı Etsy write çalıştırılmaz; stale veya belirsiz sonuçta işlem durur."));
        checks.Add(new("capability-manifest", "PASS", $"Official-only capability matrisi yüklendi: {EtsyCapabilityAudit.OfficialManifest.Count} capability; {EtsyCapabilityAudit.MissingOrBlocked().Count} preview/block."));
        checks.Add(new("connector", "WARN", "Etsy API yalnız credential ve resmi kapsam doğrulandığında çağrılır; diğer kanallar bu kontrolde değiştirilmez."));
        return new(DateTime.UtcNow, checks);
    }
}

public static class EtsyReadinessPanel
{
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null)
    {
        var root = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1350 };
        var service = new EtsyReadinessService(directory);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 10) };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 240 };
        foreach (var c in new[] { ("Kontrol", "Key", 170d), ("Durum", "Status", 100d), ("Açıklama", "Detail", 900d) }) grid.Columns.Add(new DataGridTextColumn { Header = c.Item1, Binding = new Binding(c.Item2), Width = c.Item3 });
        void Refresh() { var r = service.Build(); grid.ItemsSource = r.Checks; summary.Text = $"{r.AtUtc.ToLocalTime():g} · {r.OverallStatus}"; }
        root.Children.Add(new TextBlock { Text = "Etsy satışa hazırlık", FontSize = DesignTokens.TextSectionTitleSize, FontWeight = DesignTokens.FontWeightTitle, Margin = Spacing.TitleBlock });
        root.Children.Add(new TextBlock { Text = "Credential, ilan şablonu, ürün eşleme ve dry-run kontrolleri tek ekranda gösterilir. Bu ekran canlı marketplace değişikliği yapmaz.", TextWrapping = TextWrapping.Wrap, Margin = Spacing.HintBlock });
        var buttons = new WrapPanel(); var refresh = new Button { Content = "Kontrolleri yenile", Margin = Spacing.Control }; refresh.Click += (_, _) => { try { Refresh(); } catch (Exception e) { MessageBox.Show(AuditStore.Sanitize(e.Message)); } }; buttons.Children.Add(refresh);
        var products = new Button { Content = "Ürün havuzuna git", Margin = Spacing.Control }; products.Click += (_, _) => navigate?.Invoke("products"); buttons.Children.Add(products);
        root.Children.Add(buttons); root.Children.Add(summary); root.Children.Add(grid); Refresh();
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
    }
}
