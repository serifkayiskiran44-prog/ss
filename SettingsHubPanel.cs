using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public sealed class SettingsHubPanel : UserControl
{
    readonly ListBox sections = new() { Name = "SettingsSections", MinWidth = 205 };
    readonly List<FrameworkElement> sectionViews = new();

    public SettingsHubPanel(string? directory, BackgroundAppController? backgroundController, Action<string> navigate)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        var root = new Grid { Margin = new Thickness(10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(sections);

        var content = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetColumn(content, 1);
        root.Children.Add(content);

        AddSection(content, "Genel", BuildGeneral(backgroundController));
        AddSection(content, "Mağaza bağlantıları", BuildConnections(navigate));
        AddSection(content, "Hesap kuralları", BuildAccountRules(navigate));
        AddSection(content, "Zamanlama", BuildScheduling(navigate));
        AddSection(content, "Yerel ayarlar", Wrap("SettingsLocale", "Döviz, vergi ve yerel ayarlar", "Her mağazanın para birimi, kültür ve vergi varsayılanlarını yönetin.", LocaleSettingsPanel.Create(directory)));
        AddSection(content, "Veri ve yedek", Wrap("SettingsData", "Veri ve yedek", "Yerel veri klasörünü, güvenli yedeklemeyi ve geri yüklemeyi yönetin.", DataBackupPanel.Create(directory)));
        AddSection(content, "Sağlık ve tanılama", BuildHealth(navigate));

        sections.SelectionChanged += (_, _) => ShowSelectedSection();
        sections.SelectedIndex = 0;
        Content = root;
    }

    void AddSection(Panel host, string label, FrameworkElement view)
    {
        sections.Items.Add(new ListBoxItem { Content = label, Padding = new Thickness(10, 8, 10, 8) });
        view.Visibility = Visibility.Collapsed;
        sectionViews.Add(view);
        host.Children.Add(view);
    }

    void ShowSelectedSection()
    {
        for (var index = 0; index < sectionViews.Count; index++)
            sectionViews[index].Visibility = index == sections.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    static FrameworkElement BuildGeneral(BackgroundAppController? controller)
    {
        var panel = Stack("Genel uygulama ayarları", "MonoBridge'in pencere ve arka plan davranışını yönetin.");
        var tray = new CheckBox
        {
            Name = "SettingsCloseToTray",
            Content = "Pencere kapanınca MonoBridge'i bildirim alanında çalıştır",
            IsChecked = controller?.CloseToTray ?? false,
            IsEnabled = controller is not null,
            Margin = new Thickness(2, 10, 2, 6)
        };
        if (controller is not null)
        {
            tray.Checked += (_, _) => controller.CloseToTray = true;
            tray.Unchecked += (_, _) => controller.CloseToTray = false;
        }
        panel.Children.Add(tray);
        panel.Children.Add(Note("Arka plan işleri yalnız izin verilen okuma ve yerel otomasyon görevlerini çalıştırır; pazaryerine yazma işlemleri açık onay ister."));
        return Scroll("SettingsGeneral", panel);
    }

    static FrameworkElement BuildConnections(Action<string> navigate)
    {
        var panel = Stack("Mağaza bağlantıları", "Hesap kimlik bilgilerini bu merkezde göstermeden ilgili güvenli bağlantı ekranını açın.");
        panel.Children.Add(RouteButton("SettingsOpenConnections", "Mağaza bağlantılarını yönet", "connections", navigate));
        panel.Children.Add(RouteButton("SettingsOpenMarketplaces", "Bağlı mağazaları aç", "marketplaces", navigate));
        panel.Children.Add(RouteButton("SettingsOpenBizimHesap", "BizimHesap bağlantısını aç", "bizimhesap", navigate));
        panel.Children.Add(RouteButton("SettingsOpenNes", "NES e-Fatura bağlantısını aç", "nes", navigate));
        return Scroll("SettingsConnections", panel);
    }

    static FrameworkElement BuildAccountRules(Action<string> navigate)
    {
        var panel = Stack("Hesap kuralları", "Mağaza bazlı fiyat, stok ve sipariş kurallarını değiştirmeden önce kesin hesabı seçin.");
        panel.Children.Add(Note("Önce bağlı mağazayı açıp hesap kartını seçin. Genel kurallar mağaza kimliği olmadan uzaktan yazma başlatmaz."));
        panel.Children.Add(RouteButton("SettingsSelectAccount", "Mağaza hesabını seç", "marketplaces", navigate));
        panel.Children.Add(RouteButton("SettingsOpenPolicies", "Politika merkezini aç", "policy-center", navigate));
        return Scroll("SettingsAccountRules", panel);
    }

    static FrameworkElement BuildScheduling(Action<string> navigate)
    {
        var panel = Stack("Zamanlama ve otomasyon", "XML, sağlık ve yerel işlerin çalışma takvimlerini tek yerden yönetin.");
        panel.Children.Add(RouteButton("SettingsOpenAutomation", "Otomasyon takvimini aç", "automation", navigate));
        panel.Children.Add(RouteButton("SettingsOpenSync", "Senkronizasyon kuyruğunu aç", "sync", navigate));
        return Scroll("SettingsScheduling", panel);
    }

    static FrameworkElement BuildHealth(Action<string> navigate)
    {
        var panel = Stack("Sağlık ve tanılama", "Bağlantı durumlarını, güvenli hata ayrıntılarını ve denetim kayıtlarını inceleyin.");
        panel.Children.Add(RouteButton("SettingsOpenApiHealth", "API sağlığını aç", "api-health", navigate));
        panel.Children.Add(RouteButton("SettingsOpenReports", "Raporlar ve denetimi aç", "reports", navigate));
        return Scroll("SettingsHealth", panel);
    }

    static FrameworkElement Wrap(string name, string title, string description, UIElement child)
    {
        var panel = Stack(title, description);
        panel.Children.Add(child);
        return Scroll(name, panel);
    }

    static StackPanel Stack(string title, string description)
    {
        var panel = new StackPanel { MaxWidth = 1050, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(23, 54, 70)) });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 14) });
        return panel;
    }

    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(2, 8, 2, 8) };

    static Button RouteButton(string name, string label, string route, Action<string> navigate)
    {
        var button = new Button { Name = name, Content = label, Tag = route, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 230, Margin = new Thickness(2, 4, 2, 4), Padding = new Thickness(12, 7, 12, 7) };
        button.Click += (_, _) => navigate(route);
        return button;
    }

    static ScrollViewer Scroll(string name, UIElement child) => new()
    {
        Name = name,
        Content = child,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
}
