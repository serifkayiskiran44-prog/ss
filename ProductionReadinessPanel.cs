using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

public static class ProductionReadinessPanel
{
    public static FrameworkElement Create(string? directory)
    {
        var root = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1350 };
        var service = new ProductionReadinessService(directory);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 10) };
        var checks = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 280, EnableRowVirtualization = true };
        foreach (var column in new[] { ("Kontrol", "Key", 190d), ("Durum", "Status", 95d), ("Açıklama", "Detail", 850d) })
            checks.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = column.Item3 });

        void Refresh()
        {
            var report = service.Build();
            checks.ItemsSource = report.Checks;
            summary.Text = $"{report.AtUtc.ToLocalTime():g} · {report.Passed} geçti · {report.Warnings} uyarı · {report.Blocked} bloklu · {report.Errors} hata · " +
                (report.Ready ? "Yerel üretim geçidi hazır." : "Canlı kullanım öncesi bloklu kontroller var.");
        }

        var refresh = Button("Kontrolleri yenile", Refresh);
        root.Children.Add(Heading("Üretim hazırlığı ve son sertleştirme"));
        root.Children.Add(Hint("Bu merkez yalnız yerel veri, kuyruk, audit, secret güvenliği ve connector capability sözleşmelerini okur. Marketplace'e istek göndermez; doğrulanmamış kanallar LIVE_API_BLOCKED olarak kalır."));
        root.Children.Add(refresh);
        root.Children.Add(summary);
        root.Children.Add(checks);
        Refresh();
        return Scroll(root);
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 12) };
    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)), Margin = new Thickness(4, 8, 4, 8) };
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(AuditStore.Sanitize(error.Message), "Üretim hazırlığı", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
