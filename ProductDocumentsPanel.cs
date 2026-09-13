using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The product editor's document section (#911): the documents of the selected product with their state, a picker
/// that attaches one under a kind and an optional expiry, an opener that serves only a verified file from the store,
/// and a removal behind the destructive confirmation. Words name kinds, revisions and dates; the store's paths are
/// never shown.
/// </summary>
public static class ProductDocumentsPanel
{
    public static FrameworkElement Create(ProductDocumentStore store, Func<string?> productId, Action<string> log, Func<Window?>? owner = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(productId); ArgumentNullException.ThrowIfNull(log);
        var panel = new StackPanel();
        var list = new ListBox { MinHeight = 60, MaxHeight = 160, DisplayMemberPath = "Words" };
        var kind = new ComboBox { ItemsSource = ProductDocumentKinds.All, DisplayMemberPath = "Label", SelectedValuePath = "Key", SelectedIndex = 0, MinWidth = 220 };
        var expires = new DatePicker { MinWidth = 140 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            var id = productId();
            list.ItemsSource = string.IsNullOrWhiteSpace(id) ? Array.Empty<ProductDocumentState>() : store.States(id!, DateTime.UtcNow);
            status.Text = string.IsNullOrWhiteSpace(id) ? "Önce ürün seçin." : list.Items.Count == 0 ? "Bu ürüne belge eklenmemiş." : "";
        }
        var attach = new Button { Content = "Belge ekle (PDF, PNG, JPEG)", HorizontalAlignment = HorizontalAlignment.Left };
        attach.Click += (_, _) =>
        {
            var id = productId(); if (string.IsNullOrWhiteSpace(id)) { status.Text = "Önce ürün seçin."; return; }
            var picker = new OpenFileDialog { Filter = "Belgeler|*.pdf;*.png;*.jpg;*.jpeg", Multiselect = false, CheckFileExists = true };
            if (picker.ShowDialog() != true) return;
            try
            {
                var document = store.Attach(id!, kind.SelectedValue as string ?? "other", picker.FileName, expires.SelectedDate);
                try { new AuditStore(Path.GetDirectoryName(store.Root)!).Append(ProductDocumentStore.ToAudit(document, ProductDocumentStore.AttachAction)); } catch (Exception) { }
                log($"Belge eklendi: {ProductDocumentKinds.Label(document.Kind)} · rev. {document.Revision}.");
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException) { status.Text = AuditStore.Redact(error.Message); }
            Refresh();
        };
        var open = new Button { Content = "Seçili belgeyi aç", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) =>
        {
            if (list.SelectedItem is not ProductDocumentState selected) { status.Text = "Önce listeden bir belge seçin."; return; }
            var path = store.PathFor(selected.Document);
            if (path is null) { status.Text = "Belge dosyası eksik veya bozuk; açılamıyor."; return; }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { status.Text = "Belge açılamadı: " + AuditStore.Redact(error.Message); }
        };
        var remove = new Button { Content = "Seçili belgeyi kaldır", HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not ProductDocumentState selected) { status.Text = "Önce listeden bir belge seçin."; return; }
            if (!DialogShell.Confirm(owner?.Invoke(), "Belgeyi kaldır", $"{ProductDocumentKinds.Label(selected.Document.Kind)} (rev. {selected.Document.Revision}) kalıcı olarak silinecek.", "Kaldır")) return;
            if (store.Remove(selected.Document.Id))
            {
                try { new AuditStore(Path.GetDirectoryName(store.Root)!).Append(ProductDocumentStore.ToAudit(selected.Document, ProductDocumentStore.RemoveAction)); } catch (Exception) { }
                log("Belge kaldırıldı.");
            }
            Refresh();
        };
        // Every input carries its accessible name (the operator-flow audit refuses an unnamed control): the labels name the combo and the picker, the list names itself.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var kindLabel = new TextBlock { Text = "Belge türü", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; row.Children.Add(kindLabel); row.Children.Add(kind); FormField.Labelled(kindLabel, kind);
        var expiresLabel = new TextBlock { Text = "Geçerlilik (isteğe bağlı)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) }; row.Children.Add(expiresLabel); row.Children.Add(expires); FormField.Labelled(expiresLabel, expires);
        System.Windows.Automation.AutomationProperties.SetName(list, "Ürünün uygunluk belgeleri");
        System.Windows.Automation.AutomationProperties.SetName(status, "Belge durumu");
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(attach); actions.Children.Add(open); actions.Children.Add(remove);
        panel.Children.Add(list); panel.Children.Add(row); panel.Children.Add(actions); panel.Children.Add(status);
        panel.Loaded += (_, _) => Refresh(); panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) Refresh(); };
        return new GroupBox { Header = "Uygunluk belgeleri", Content = panel, Tag = "product-documents" };
    }
}
