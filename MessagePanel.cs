using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class MessagePanel
{
    public static FrameworkElement Create(string? directory = null, Action<string>? navigate = null)
    {
        var store = new MessageStore(directory); var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(Text("Mesaj ve müşteri iletişim merkezi", TextRole.SectionTitle));
        top.Children.Add(Text("Mesajlar yerel olarak saklanır. Doğrulanmış mesaj API capability'si bulunmayan kanallarda gerçek okuma/yazma yapılmaz."));
        var bar = new WrapPanel(); top.Children.Add(bar);
        var query = new TextBox { Width = 240, ToolTip = "Kanal, mağaza, müşteri, sipariş, konu veya metin ara" }; bar.Children.Add(query);
        var marketplace = new ComboBox { Width = 130, ItemsSource = new[] { "Tümü", "etsy", "ebay", "amazon", "trendyol", "hepsiburada", "ozon", "allegro", "joom", "wish", "fruugo", "Yerel" }, SelectedIndex = 0 }; bar.Children.Add(marketplace);
        var state = new ComboBox { Width = 120, ItemsSource = new[] { "Tümü", "Unread", "Read", "Draft", "Failed" }, SelectedIndex = 0 }; bar.Children.Add(state);
        var refresh = Button("Yenile"); bar.Children.Add(refresh);
        var add = Button("+ Yerel mesaj"); bar.Children.Add(add);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true, Height = 370, SelectionMode = DataGridSelectionMode.Single };
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        foreach (var column in new[] { ("Kanal", "Marketplace", 90d), ("Mağaza", "ShopId", 105d), ("Müşteri", "Customer", 150d), ("Sipariş", "OrderId", 115d), ("Konu", "Subject", 250d), ("Durum", "Status", 90d), ("Yön", "Direction", 90d), ("Güncelleme", "UpdatedUtc", 155d) }) grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = column.Item3 });
        var statusText = Text("Mesajlar yükleniyor…"); top.Children.Add(statusText);
        var detail = new StackPanel { Margin = new Thickness(14, 0, 0, 0) }; var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(430) }); layout.Children.Add(grid); var detailScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(detailScroll, 1); layout.Children.Add(detailScroll); root.Children.Add(layout);
        var info = Text("Bir mesaj seçin."); detail.Children.Add(Text("Mesaj ayrıntısı", TextRole.SubsectionTitle)); detail.Children.Add(info);
        var channel = Field(detail, "Pazaryeri", "Yerel"); var shop = Field(detail, "Mağaza", "Mağazam"); var external = Field(detail, "Harici mesaj ID (opsiyonel)", ""); var order = Field(detail, "Sipariş no (opsiyonel)", ""); var product = Field(detail, "Ürün / SKU bağlamı (opsiyonel)", ""); var customer = Field(detail, "Müşteri", ""); var subject = Field(detail, "Konu", ""); var body = Field(detail, "Mesaj", "", 130); var direction = new ComboBox { ItemsSource = new[] { "Inbound", "Outbound" }, SelectedIndex = 0 }; Label(detail, "Yön", direction);
        var capability = Text("Kanal seçince mesaj API capability durumu görünür."); detail.Children.Add(capability);
        var selected = (MessageRecord?)null; List<MessageRecord> all = [];
        var templateBox = new ComboBox { DisplayMemberPath = "Name", Width = 250 }; var templateName = new TextBox { Width = 180, ToolTip = "Yerel şablon adı" }; var templateBody = new TextBox { Width = 280, Height = 55, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }; var templateRow = new WrapPanel(); templateRow.Children.Add(Text("Yerel şablon")); templateRow.Children.Add(templateBox); templateRow.Children.Add(templateName); templateRow.Children.Add(templateBody); var saveTemplate = Button("Şablonu kaydet"); var deleteTemplate = Button("Şablonu sil"); templateRow.Children.Add(saveTemplate); templateRow.Children.Add(deleteTemplate); detail.Children.Add(templateRow);
        void ReloadTemplates() => templateBox.ItemsSource = store.Templates();
        void ClearEditor() { selected = null; channel.Text = "Yerel"; shop.Text = "Mağazam"; external.Clear(); order.Clear(); product.Clear(); customer.Clear(); subject.Clear(); body.Clear(); direction.SelectedIndex = 0; info.Text = "Yeni yerel yardımcı mesaj. Kaydetmek API'ye göndermez."; capability.Text = MessageCapabilityService.Describe(channel.Text); }
        void Edit(MessageRecord message) { selected = message; channel.Text = message.Marketplace; shop.Text = message.ShopId; external.Text = message.ExternalId; order.Text = message.OrderId; product.Text = message.ProductId; customer.Text = message.Customer; subject.Text = message.Subject; body.Text = message.Body; direction.SelectedItem = message.Direction; info.Text = $"{message.Status} · {message.CreatedUtc.ToLocalTime():g} · {message.Id}"; capability.Text = MessageCapabilityService.Describe(message.Marketplace); }
        void Reload()
        {
            var selectedStatus = state.SelectedItem?.ToString() ?? "Tümü"; var selectedChannel = marketplace.SelectedItem?.ToString() ?? "Tümü"; MessageStatus? parsed = selectedStatus == "Tümü" ? null : Enum.TryParse<MessageStatus>(selectedStatus, out var value) ? value : null; all = store.List(selectedChannel == "Tümü" ? null : selectedChannel, null, parsed, query.Text).ToList(); grid.ItemsSource = all; statusText.Text = $"{all.Count:N0} mesaj · Okunmamış: {all.Count(x => x.Status == MessageStatus.Unread)} · gerçek mesaj API write/read kapalı";
        }
        var save = Button("Yerel mesajı kaydet"); save.Click += (_, _) => { try { var message = selected ?? new MessageRecord(); message.Marketplace = channel.Text.Trim(); message.ShopId = shop.Text.Trim(); message.ExternalId = external.Text.Trim(); message.OrderId = order.Text.Trim(); message.ProductId = product.Text.Trim(); message.Customer = customer.Text.Trim(); message.Subject = subject.Text.Trim(); message.Body = body.Text; message.Direction = direction.SelectedItem?.ToString() ?? "Inbound"; message.Status = message.Direction == "Outbound" ? MessageStatus.Draft : MessageStatus.Unread; store.Upsert(message); AuditStoreAppend(directory, message); Reload(); Edit(store.Get(message.Id)!); statusText.Text = "Mesaj yerel olarak kaydedildi; pazaryerine gönderilmedi."; } catch (Exception error) { statusText.Text = MarketplaceConnectionStore.Redact(error.Message); } }; detail.Children.Add(save);
        var markRead = Button("Okundu işaretle"); markRead.Click += (_, _) => { if (selected is null) { statusText.Text = "Önce mesaj seçin."; return; } store.SetStatus(selected.Id, MessageStatus.Read); Reload(); Edit(store.Get(selected.Id)!); }; detail.Children.Add(markRead);
        var previewReply = Button("Yanıtı önizle (gönderme yok)"); previewReply.Click += (_, _) => { if (selected is null) { statusText.Text = "Yanıt için mesaj seçin."; return; } var capabilityText = MessageCapabilityService.Describe(selected.Marketplace); statusText.Text = $"{capabilityText} Yanıt yalnızca yerel taslak olarak kaydedilebilir."; }; detail.Children.Add(previewReply);
        var openOrder = Button("Sipariş ekranına git"); openOrder.Click += (_, _) => navigate?.Invoke("orders"); var openProduct = Button("Ürün ekranına git"); openProduct.Click += (_, _) => navigate?.Invoke("products"); detail.Children.Add(new WrapPanel { Children = { openOrder, openProduct } });
        templateBox.SelectionChanged += (_, _) => { if (templateBox.SelectedItem is MessageTemplate template) { templateName.Text = template.Name; templateBody.Text = template.Body; body.Text = template.Body; } }; saveTemplate.Click += (_, _) => { try { store.SaveTemplate(templateName.Text, templateBody.Text); ReloadTemplates(); } catch (Exception error) { statusText.Text = MarketplaceConnectionStore.Redact(error.Message); } }; deleteTemplate.Click += (_, _) => { if (templateBox.SelectedItem is MessageTemplate template) { store.DeleteTemplate(template.Name); ReloadTemplates(); } }; grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is MessageRecord message) Edit(message); }; query.TextChanged += (_, _) => Reload(); marketplace.SelectionChanged += (_, _) => Reload(); state.SelectionChanged += (_, _) => Reload(); refresh.Click += (_, _) => Reload(); add.Click += (_, _) => ClearEditor(); ReloadTemplates(); Reload(); ClearEditor(); return root;
    }
    static void AuditStoreAppend(string? directory, MessageRecord message)
    {
        try { new AuditStore(directory).Append(new() { Module = "messages", Action = message.Direction == "Outbound" ? "draft" : "local", Marketplace = message.Marketplace, ShopId = message.ShopId, OrderId = message.OrderId, Outcome = "Info", Detail = "Mesaj gövdesi audit kaydına yazılmadı." }); } catch { }
    }
    static TextBlock Text(string value, TextRole role = TextRole.Body) => TextStyles.Apply(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 4, 3, 7), Foreground = Brushes.DarkSlateGray }, role);
    static TextBox Field(Panel panel, string label, string value, int height = 0) { panel.Children.Add(Text(label)); var box = new TextBox { Text = value }; if (height > 0) { box.Height = height; box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; } panel.Children.Add(box); return box; }
    static void Label(Panel panel, string label, UIElement control) { panel.Children.Add(Text(label)); panel.Children.Add(control); }
    static Button Button(string text) => new() { Content = text, Margin = new Thickness(3, 5, 3, 5) };
}
