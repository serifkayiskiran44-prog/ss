using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

public static class NesQueueSelectionPolicy
{
    public static IReadOnlyList<NesQueueItem> Retryable(IEnumerable<NesQueueItem> rows) => rows.Where(x => x.State == NesQueueState.RetryWaiting).ToList();
    public static IReadOnlyList<NesQueueItem> Cancellable(IEnumerable<NesQueueItem> rows) => rows.Where(x => x.State is NesQueueState.Planned or NesQueueState.Ready or NesQueueState.RetryWaiting or NesQueueState.Blocked).ToList();
}

public static class NesQueuePanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new NesInvoiceQueueStore(directory); var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "NES işlem kuyruğu", FontSize = 22, Margin = new Thickness(4) });
        var actions = new WrapPanel(); top.Children.Add(actions);
        Button Add(string text) { var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) }; actions.Children.Add(button); return button; }
        var run = Add("Kuyruğu çalıştır"); var retry = Add("Yeniden dene"); var reconcile = Add("Mutabakat gerekli"); var cancel = Add("İptal et"); var refresh = Add("Yenile");
        var status = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap }; top.Children.Add(status);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, SelectionMode = DataGridSelectionMode.Extended }; root.Children.Add(grid);
        void Load() { var rows = store.List(); grid.ItemsSource = rows; status.Text = $"{rows.Count} kayıt · {rows.Count(x => x.State == NesQueueState.Unknown)} mutabakat gerekli · {rows.Count(x => x.State == NesQueueState.RetryWaiting)} yeniden deneme bekliyor"; }
        run.Click += (_, _) => { NesQueueRuntime.Wake(); status.Text = "Kuyruk çalıştırma isteği gönderildi."; };
        retry.Click += (_, _) => { foreach (var row in NesQueueSelectionPolicy.Retryable(grid.SelectedItems.Cast<NesQueueItem>())) store.Retry(row.Id, DateTimeOffset.UtcNow); NesQueueRuntime.Wake(); Load(); };
        cancel.Click += (_, _) => { foreach (var row in NesQueueSelectionPolicy.Cancellable(grid.SelectedItems.Cast<NesQueueItem>())) store.Cancel(row.Id, DateTimeOffset.UtcNow); Load(); };
        reconcile.Click += (_, _) => { var count = store.List().Count(x => x.State == NesQueueState.Unknown); MessageBox.Show($"{count} kayıt yeniden gönderilemez. NES giden faturalar ekranından sipariş numarasıyla mutabakat yapın.", "NES mutabakatı", MessageBoxButton.OK, MessageBoxImage.Information); };
        refresh.Click += (_, _) => Load(); Load(); return root;
    }
}
