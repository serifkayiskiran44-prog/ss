using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

/// <summary>The searchable shortcut reference (#869): a filter box that already has the keyboard, the catalogue's entries with their gestures and conditions, Escape to close.</summary>
public static class KeyboardShortcutReference
{
    public const string Title = "Klavye kısayolları";

    public static Window Build(Window? owner)
    {
        var filter = new TextBox { Tag = "shortcut-filter", Margin = Spacing.BelowControl, ToolTip = "Kısayol, komut veya tuş ara" };
        AutomationProperties.SetName(filter, "Kısayol ara");
        var list = new ListBox { Tag = "shortcut-list", MinHeight = 240 };
        AutomationProperties.SetName(list, "Kısayol listesi");
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("."));
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        list.ItemTemplate = new DataTemplate { VisualTree = factory };
        var empty = TextStyles.Create(TextRole.Hint, "Aramaya uyan kısayol yok."); empty.Visibility = Visibility.Collapsed;
        void Refresh()
        {
            var entries = KeyboardShortcuts.Filter(filter.Text);
            list.ItemsSource = entries; empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        filter.TextChanged += (_, _) => Refresh(); Refresh();
        var note = TextStyles.Create(TextRole.Hint, "Yıkıcı veya pazaryerine canlı yazan işlemlerin kısayolu yoktur; onay pencereleri klavyeden atlanamaz.");
        note.TextWrapping = TextWrapping.Wrap; note.Margin = Spacing.AboveControl;
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(filter, Dock.Top); DockPanel.SetDock(note, Dock.Bottom); DockPanel.SetDock(empty, Dock.Bottom);
        body.Children.Add(filter); body.Children.Add(note); body.Children.Add(empty); body.Children.Add(list);
        var window = DialogShell.Create(owner, Title, body, new[] { new DialogShell.Action("Kapat", IsPrimary: true, IsCancel: true) }, 560, 520);
        window.Loaded += (_, _) => { filter.Focus(); Keyboard.Focus(filter); };
        return window;
    }

    public static void Show(Window owner) => Build(owner).ShowDialog();
}
