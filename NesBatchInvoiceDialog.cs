using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

public sealed record NesBatchInvoiceViewState(int ReadyCount, int BlockedCount, bool CanApprove)
{
    public static NesBatchInvoiceViewState From(NesQueuePlan plan) => new(plan.ReadyCount, plan.BlockedCount, plan.ReadyCount > 0);
}

public static class NesBatchInvoiceDialog
{
    public static int Show(Window? owner, IReadOnlyList<OrderSnapshot> orders, string? directory)
    {
        var settingsPath = directory is null ? null : Path.Combine(directory, "nes.bin");
        var settings = new NesSettingsStore(settingsPath).Load() ?? throw new InvalidOperationException("Önce NES e-Fatura ekranında bağlantı ve firma ayarlarını kaydedin.");
        var aliases = new NesRecipientAliasStore(directory); var queue = new NesInvoiceQueueStore(directory);
        NesQueuePlan plan = NesQueuePlanner.Build(orders, settings, aliases.Get, true);
        var win = new Window { Title = "NES toplu fatura önizlemesi", Width = 1120, Height = 720, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        if (owner is not null) win.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(12) }; win.Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var summary = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
        top.Children.Add(summary);
        var aliasBar = new WrapPanel(); top.Children.Add(aliasBar);
        aliasBar.Children.Add(new TextBlock { Text = "Kurumsal VKN/TCKN", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
        var tax = new TextBox { Width = 140, Margin = new Thickness(4) }; aliasBar.Children.Add(tax);
        aliasBar.Children.Add(new TextBlock { Text = "NES alıcı etiketi", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
        var alias = new TextBox { Width = 300, Margin = new Thickness(4), ToolTip = "Örnek: urn:mail:pk@firma" }; aliasBar.Children.Add(alias);
        var saveAlias = new Button { Content = "Etiketi kaydet ve yeniden hesapla", Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) }; aliasBar.Children.Add(saveAlias);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, ItemsSource = plan.Rows, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(grid);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var approval = new CheckBox { Margin = new Thickness(4), Content = "Hazır siparişler için gerçek NES faturalarının sırayla gönderileceğini onaylıyorum." }; bottom.Children.Add(approval);
        var buttons = new WrapPanel(); bottom.Children.Add(buttons);
        var confirm = new Button { Content = "Onayla ve NES kuyruğuna ekle", Margin = new Thickness(4), Padding = new Thickness(14, 7, 14, 7), IsEnabled = false }; buttons.Children.Add(confirm);
        var close = new Button { Content = "Vazgeç", Margin = new Thickness(4), Padding = new Thickness(14, 7, 14, 7) }; buttons.Children.Add(close);
        var added = 0;
        void Refresh()
        {
            plan = NesQueuePlanner.Build(orders, settings, aliases.Get, true); grid.ItemsSource = plan.Rows;
            var state = NesBatchInvoiceViewState.From(plan); summary.Text = $"{orders.Count} seçili sipariş · {state.ReadyCount} hazır · {state.BlockedCount} engelli. Engelli satırlar düzeltilmeden kuyruğa alınmaz.";
            confirm.IsEnabled = state.CanApprove && approval.IsChecked == true;
        }
        saveAlias.Click += (_, _) => { try { aliases.Save(tax.Text, alias.Text, DateTimeOffset.UtcNow); Refresh(); } catch (Exception ex) { MessageBox.Show(win, MarketplaceConnectionStore.Redact(ex.Message), "NES alıcı etiketi", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        approval.Checked += (_, _) => Refresh(); approval.Unchecked += (_, _) => Refresh();
        confirm.Click += (_, _) => { foreach (var row in plan.Rows.Where(x => x.Request is not null)) { queue.Enqueue(row.Request!, DateTimeOffset.UtcNow); added++; } NesQueueRuntime.Wake(); win.DialogResult = true; win.Close(); };
        close.Click += (_, _) => win.Close(); Refresh(); win.ShowDialog(); return added;
    }
}
