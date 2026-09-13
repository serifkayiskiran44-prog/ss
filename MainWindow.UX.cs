using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleShortcut(key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// #869: the shell's shortcuts come from the one catalogue the reference and the tooltips read. Returns whether
    /// the press did something; a disabled command (Back with no trail, Escape away from a toast) is not handled,
    /// so the key keeps its ordinary meaning.
    /// </summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        var command = KeyboardShortcuts.Match(key, modifiers, ShortcutScope.Shell)?.CommandKey;
        if (command is null) return false;
        using var activity = UiActivity.Enter(command);
        switch (command)
        {
            case "global-search": GlobalSearchBox.Focus(); GlobalSearchBox.SelectAll(); return true;
            case "navigate-dashboard": Navigate("dashboard"); return true;
            case "navigate-products": Navigate("products"); return true;
            // #814: Escape closes the newest toast when the keyboard is in the toast host; elsewhere Escape keeps its owner.
            case "dismiss-toast": if (!ToastHost.IsKeyboardFocusWithin) return false; DismissTopToast(); return true;
            // #812: the sidebar collapses from the keyboard too.
            case "toggle-sidebar": ToggleSidebar(); return true;
            // #810: the drill-through trail is walkable from the keyboard, not only from the "‹ Geri" button.
            case "back": if (!BackButton.IsEnabled) return false; Back_Click(BackButton, new RoutedEventArgs()); return true;
            case "refresh-products": RefreshProducts(); return true;
            case "shortcut-reference": KeyboardShortcutReference.Show(this); return true;
            default: return false;
        }
    }

    void ShortcutsButton_Click(object sender, RoutedEventArgs e) => KeyboardShortcutReference.Show(this);

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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); globalSearchCts = cancellation; var revision = ++globalSearchRevision; CommandState.Apply(GlobalSearchButton, DisabledReason.Busy("Arama sürüyor.")); StatusText.Text = "Yerel arama hazırlanıyor…";
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
            if (ReferenceEquals(globalSearchCts, cancellation)) { globalSearchCts = null; CommandState.Apply(GlobalSearchButton, null); }
        }
    }

    async Task WarmGlobalSearchAsync()
    {
        try { await globalSearchIndex.EnsureFreshAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log($"Global arama indeksi hazırlanamadı: {Safe(error)}", NotificationSeverity.Error); }
    }

    void ShowGlobalResults(string query, IReadOnlyList<GlobalSearchHit> hits)
    {
        Window window = null!;
        var root = new DockPanel { Margin = new Thickness(0) };
        var header = new TextBlock { Text = hits.Count == 0 ? "Eşleşme bulunamadı." : $"{hits.Count} eşleşme · bir sonuca tıklayarak ilgili ekrana git", Margin = new Thickness(3, 3, 3, 10), Foreground = System.Windows.Media.Brushes.DarkSlateGray };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top); root.Children.Add(header);
        var list = new ListBox { BorderThickness = new Thickness(0) };
        foreach (var hit in hits)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.DarkSlateGray, Content = new StackPanel { Children = { new TextBlock { Text = $"{hit.Type}  ·  {hit.Title}", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = hit.Detail, Foreground = new System.Windows.Media.SolidColorBrush(DesignTokens.TextMutedColor), Margin = new Thickness(0, 3, 0, 0) } } } };
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
        root.Children.Add(list);
        // #818: the standard dialog shell -- fitted to the work area, resizable, body scrolls, Kapat on Escape.
        window = DialogShell.Create(this, $"Arama · {query}", root, new DialogShell.Action[] { new("Kapat", IsCancel: true) }, 720, 560);
        window.ShowDialog();
    }
}
