using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>Marketplace setup with eBay OAuth and read-only verification.</summary>
public static class MarketplaceSetupPanel
{
    public static FrameworkElement Create()
    {
        var content = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1050, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(Text("Pazaryeri bağlantı hazırlığı", 25));
        content.Children.Add(Text("eBay OAuth ve Ozon Seller API için salt okunur bağlantı doğrulaması; diğer kanallar için hesap ve API başvuru adımları. Ürün, stok ve sipariş aktarımı etkin değil."));
        content.Children.Add(Text("Ürünler mevcut ortak havuzda kalır. Hesap açılışı tamamlandıktan sonra API yetkilendirmesi ve gerçek bağlantı testi gerekir."));
        content.Children.Add(MarketplaceImagePanel.Create());
        foreach (var channel in MarketplaceRegistry.All)
        {
            var body = new StackPanel();
            if (channel.Id == "ebay") body.Children.Add(EbayPanel.Create());
            else if (channel.Id == "ozon") body.Children.Add(OzonPanel.Create());
            else body.Children.Add(Text(channel.Status, 15, new SolidColorBrush(DesignTokens.WarningTextColor)));
            body.Children.Add(Text(channel.Prerequisites));
            var result = Text("");
            var actions = new WrapPanel();
            actions.Children.Add(Link("Hesap / başvuru sayfasını aç", channel.RegistrationUrl, result));
            actions.Children.Add(Link("Resmî API belgelerini aç", channel.DocumentationUrl, result));
            body.Children.Add(actions);
            body.Children.Add(result);
            content.Children.Add(new GroupBox { Header = channel.Name, Content = body, Margin = Spacing.VerticalControl, Padding = Spacing.Section });
        }
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <param name="directory">The data directory the connection stores live in (null: the profile default); #855 passes the shell's so tests never touch the real profile.</param>
    public static FrameworkElement CreateChannel(string id, string? directory=null, SettingsEditState? editState=null)
    {
        var channel=MarketplaceRegistry.All.Single(c=>c.Id==id);
        var content=new StackPanel{Margin=new Thickness(16)};
        content.Children.Add(Text(channel.Name+" bağlantısı",22));
        if(id=="ebay")content.Children.Add(EbayPanel.Create(directory,editState));
        else if(id=="ozon")content.Children.Add(OzonPanel.Create(directory,editState));
        else content.Children.Add(Text(id=="joom"?"Satıcı kaydı / kabulü bekleniyor. API bağlantısı kurulmadı.":channel.Status,15,new SolidColorBrush(DesignTokens.WarningTextColor)));
        content.Children.Add(Text(channel.Prerequisites));
        var result=Text("");var actions=new WrapPanel();
        actions.Children.Add(Link("Satıcı hesabı / başvuru",channel.RegistrationUrl,result));
        actions.Children.Add(Link("Resmî API belgeleri",channel.DocumentationUrl,result));
        content.Children.Add(actions);content.Children.Add(result);
        return new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    public static FrameworkElement CreateOther(string? directory=null)
    {
        var tabs=new TabControl();
        foreach(var id in new[]{"wish","allegro","fruugo"}) { var channelTabs=new TabControl();channelTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create(id,directory)});channelTabs.Items.Add(new TabItem{Header="Bağlantı",Content=CreateChannel(id)});tabs.Items.Add(new TabItem{Header=MarketplaceRegistry.All.Single(c=>c.Id==id).Name,Content=channelTabs}); }
        return tabs;
    }
    static TextBlock Text(string value, double size = 14, Brush? color = null) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = color ?? Brushes.DarkSlateGray, Margin = Spacing.BelowControl
    };

    static Button Link(string label, string url, TextBlock result)
    {
        var button = new Button { Content = label, ToolTip = url };
        button.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                result.Text = "Sayfa tarayıcıya gönderildi. Hesap veya API bağlantısı bu işlemle doğrulanmaz.";
            }
            catch { result.Text = "Tarayıcı açılamadı. Adres: " + url; }
        };
        return button;
    }
}


