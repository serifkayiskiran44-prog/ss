using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            GlobalSearchBox.Focus(); GlobalSearchBox.SelectAll(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.NumPad1) { Navigate("dashboard"); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D2 or Key.NumPad2) { Navigate("products"); e.Handled = true; return; }
        // #812: the sidebar collapses from the keyboard too.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) { ToggleSidebar(); e.Handled = true; return; }
        // #810: the drill-through trail is walkable from the keyboard, not only from the "‹ Geri" button.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left && BackButton.IsEnabled) { Back_Click(BackButton, e); e.Handled = true; return; }
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { RefreshProducts(); e.Handled = true; }
    }

    void GlobalSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GlobalSearch_Click(sender, e); e.Handled = true; }
    }

    async void SearchTimer_Tick(object? sender, EventArgs e)
    {
        globalSearchTimer.Stop();
        if (GlobalSearchBox.IsKeyboardFocusWithin) await GlobalSearch_ClickAsync();
    }

    async void GlobalSearch_Click(object? sender, RoutedEventArgs e) => await GlobalSearch_ClickAsync();

    async Task GlobalSearch_ClickAsync()
    {
        var query = GlobalSearchBox.Text.Trim();
        if (query.Length < 2) { StatusText.Text = "Arama için en az 2 karakter girin."; GlobalSearchBox.Focus(); return; }
        var previous = globalSearchCts; globalSearchCts = null; previous?.Cancel(); previous?.Dispose();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); globalSearchCts = cancellation; var revision = ++globalSearchRevision; GlobalSearchButton.IsEnabled = false; StatusText.Text = "Yerel arama hazırlanıyor…";
        try
        {
            await Task.Delay(150, cancellation.Token);
            var hits = await globalSearchIndex.SearchAsync(query, 80, cancellation.Token);
            if (revision != globalSearchRevision || cancellation.IsCancellationRequested) return;
            ShowGlobalResults(query, hits);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (revision == globalSearchRevision) StatusText.Text = $"Arama yapılamadı: {Safe(error)}"; }
        finally
        {
            if (ReferenceEquals(globalSearchCts, cancellation)) { globalSearchCts = null; GlobalSearchButton.IsEnabled = true; }
        }
    }

    async Task WarmGlobalSearchAsync()
    {
        try { await globalSearchIndex.EnsureFreshAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log($"Global arama indeksi hazırlanamadı: {Safe(error)}"); }
    }

    void ShowGlobalResults(string query, IReadOnlyList<GlobalSearchHit> hits)
    {
        var window = new Window { Owner = this, Title = $"Arama · {query}", Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock { Text = hits.Count == 0 ? "Eşleşme bulunamadı." : $"{hits.Count} eşleşme · bir sonuca tıklayarak ilgili ekrana git", Margin = new Thickness(3, 3, 3, 10), Foreground = System.Windows.Media.Brushes.DarkSlateGray };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top); root.Children.Add(header);
        var list = new ListBox { BorderThickness = new Thickness(0) };
        foreach (var hit in hits)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.DarkSlateGray, Content = new StackPanel { Children = { new TextBlock { Text = $"{hit.Type}  ·  {hit.Title}", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = hit.Detail, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 3, 0, 0) } } } };
            // #813: a hit that names a workspace entity opens it on the trail (selected, with Back); any other hit
            // is a plain screen jump, as before.
            var found = hit;
            button.Click += (_, _) =>
            {
                window.Close();
                var target = WorkspaceLinks.FromSearchHit(found, route => routeTitles.TryGetValue(route, out var t) ? t : route);
                if (target is null || !OpenWorkspaceLink(target with { EntityLabel = found.Title })) Navigate(found.Route);
            };
            list.Items.Add(button);
        }
        root.Children.Add(list); window.Content = root; window.ShowDialog();
    }
}
