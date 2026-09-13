using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #886 (DESIGN: Empty-state illustration-free standard). Every workspace's empty state has the same shape — a text
// glyph, a title, a reason, at most one action — with a filtered empty told apart from a true empty and from a
// hidden one; an action is offered only when this build can perform it; long Turkish text wraps instead of clipping
// at a narrow width; the renderer collapses its host when there is nothing to say.
[TestClass]
public sealed class EmptyStateTests
{
    [TestMethod]
    public void StatesTellFilteredFromTrueEmptyOfferOnlySupportedActionsAndRenderWithoutClipping()
    {
        Func<string, bool> routes = r => r is "xml" or "connections" or "products";

        // A route this build does not have drops the action and keeps the diagnosis.
        var unsupported = EmptyState.Compose(EmptyStateKind.TrueEmpty, "Başlık", "Neden", "Raporları aç", "reports", routes);
        Assert.IsFalse(unsupported.HasAction); Assert.AreEqual("", unsupported.Route); Assert.AreEqual("Başlık", unsupported.Title);
        Assert.IsTrue(EmptyState.Compose(EmptyStateKind.TrueEmpty, "B", "N", "Aç", "xml", routes).HasAction);
        Assert.IsFalse(EmptyState.Compose(EmptyStateKind.TrueEmpty, "B", "N", "Aç", "xml", null).HasAction, "no route check, no promise");
        var local = EmptyState.Local(EmptyStateKind.FilteredEmpty, "B", "N", "Temizle", EmptyState.ClearFilter);
        Assert.IsTrue(local.HasAction); Assert.AreEqual(EmptyState.ClearFilter, local.ActionKey); Assert.AreEqual("", local.Route);
        Assert.IsFalse(EmptyState.Local(EmptyStateKind.TrueEmpty, "B", "N").HasAction);

        // The workspaces: filtered versus true empty, each with its own next step; the glyph follows the kind, never an image.
        var products = EmptyState.Products(filtered: false, routes); var productsFiltered = EmptyState.Products(filtered: true, routes);
        Assert.AreEqual(EmptyStateKind.TrueEmpty, products.Kind); Assert.AreEqual("xml", products.Route); StringAssert.Contains(products.Title, "Henüz ürün yok");
        Assert.AreEqual(EmptyStateKind.FilteredEmpty, productsFiltered.Kind); Assert.AreEqual(EmptyState.ClearFilter, productsFiltered.ActionKey); StringAssert.Contains(productsFiltered.Title, "uyan ürün yok");
        Assert.AreNotEqual(products.Glyph, productsFiltered.Glyph);
        var orders = EmptyState.Orders(false, routes); Assert.AreEqual("connections", orders.Route); StringAssert.Contains(orders.Title, "Henüz sipariş yok");
        Assert.IsFalse(EmptyState.Orders(false, _ => false).HasAction, "no connections screen, no button");
        Assert.AreEqual(EmptyState.ClearFilter, EmptyState.Orders(true, routes).ActionKey);
        var sources = EmptyState.Sources(); Assert.AreEqual(EmptyState.NewSource, sources.ActionKey); StringAssert.Contains(sources.Title, "XML kaynağı yok");
        Assert.AreEqual(EmptyStateKind.TrueEmpty, EmptyState.Preview(false).Kind); Assert.AreEqual(EmptyStateKind.FilteredEmpty, EmptyState.Preview(true).Kind);
        var none = EmptyState.Reports(new ReportCatalogView(Array.Empty<ReportCard>(), 0, 0), false, routes); Assert.AreEqual(EmptyStateKind.TrueEmpty, none.Kind); Assert.IsFalse(none.HasAction);
        var hidden = EmptyState.Reports(new ReportCatalogView(Array.Empty<ReportCard>(), 3, 3), false, routes); Assert.AreEqual(EmptyStateKind.Hidden, hidden.Kind); Assert.AreEqual("connections", hidden.Route);
        var searched = EmptyState.Reports(new ReportCatalogView(Array.Empty<ReportCard>(), 0, 3), true, routes); Assert.AreEqual(EmptyStateKind.FilteredEmpty, searched.Kind); Assert.AreEqual(EmptyState.ClearFilter, searched.ActionKey);
        var ladder = EmptyState.Dashboard(DashboardEmptyState.Evaluate(new DashboardEmptyInput { Connections = 0 }, routes));
        Assert.AreEqual(EmptyStateKind.TrueEmpty, ladder.Kind); Assert.AreEqual("connections", ladder.Route); StringAssert.Contains(ladder.Title, "bağlı mağaza yok");
        var filteredLadder = EmptyState.Dashboard(DashboardEmptyState.Evaluate(new DashboardEmptyInput { Connections = 1, Sources = 1, SourcesEverRun = 1, SourcesWithSuccessfulFeed = 1, Filtered = true, VisibleRecords = 0, Products = 5 }, _ => true));
        Assert.AreEqual(EmptyStateKind.FilteredEmpty, filteredLadder.Kind);
        Assert.AreEqual(EmptyStateKind.FilteredEmpty, EmptyState.Matrix(true, "Filtre").Kind);

        // Text bounds and redaction: a title is capped, a reason carrying personal data is masked.
        Assert.IsTrue(EmptyState.Compose(EmptyStateKind.TrueEmpty, new string('a', 200), "n").Title.Length <= EmptyState.MaxTitle);
        StringAssert.Contains(EmptyState.Compose(EmptyStateKind.TrueEmpty, "b", "ali@example.com yazdı").Reason, "[pii-email]");

        // Rendering in a real window: the tags, one action that navigates or runs the local action, a collapsed host for nothing, and long text that wraps rather than clips at 300 DIP.
        RunSta(() =>
        {
            Window window = null;
            try
            {
                var host = new StackPanel();
                window = new Window { Content = host, Width = 900, Height = 400, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                window.Show(); window.UpdateLayout();
                var navigated = ""; var localKey = "";
                EmptyStatePanel.Render(host, products, r => navigated = r, k => localKey = k); window.UpdateLayout();
                var border = host.Children.OfType<Border>().Single(b => b.Tag as string == EmptyState.Tag);
                Assert.IsNotNull(Descendants(border).OfType<TextBlock>().Single(t => t.Tag as string == EmptyState.TitleTag));
                Assert.AreEqual(products.Reason, Descendants(border).OfType<TextBlock>().Single(t => t.Tag as string == EmptyState.ReasonTag).Text);
                Assert.IsFalse(Descendants(border).OfType<Image>().Any(), "illustration-free");
                var action = Descendants(border).OfType<Button>().Single(b => b.Tag as string == EmptyState.ActionTag);
                Assert.IsTrue(action.Focusable); Assert.IsTrue(action.MinHeight >= DesignTokens.HitTargetMinSize);
                action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.AreEqual("xml", navigated);
                EmptyStatePanel.Render(host, productsFiltered, r => navigated = r, k => localKey = k); window.UpdateLayout();
                Descendants(host).OfType<Button>().Single(b => b.Tag as string == EmptyState.ActionTag).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.AreEqual(EmptyState.ClearFilter, localKey);
                EmptyStatePanel.Render(host, unsupported, r => navigated = r); window.UpdateLayout();
                Assert.IsFalse(Descendants(host).OfType<Button>().Any(), "no button for an action this build cannot perform");
                EmptyStatePanel.Render(host, null, null); Assert.AreEqual(Visibility.Collapsed, host.Visibility); Assert.AreEqual(0, host.Children.Count);

                var longState = EmptyState.Compose(EmptyStateKind.TrueEmpty, "Çok uzun bir Türkçe başlık: şükela ürünlerin havuzda görünmesi için içe aktarma", string.Concat(Enumerable.Repeat("Uzun bir açıklama cümlesi, ğüşıöç harfleriyle. ", 6)), "XML kaynaklarını aç", "xml", routes);
                host.Width = 300; EmptyStatePanel.Render(host, longState, r => navigated = r); window.UpdateLayout();
                var narrow = host.Children.OfType<Border>().Single();
                narrow.Measure(new Size(300, double.PositiveInfinity)); var narrowHeight = narrow.DesiredSize.Height;
                narrow.Measure(new Size(900, double.PositiveInfinity)); var wideHeight = narrow.DesiredSize.Height;
                Assert.IsTrue(narrowHeight > wideHeight, $"the text wraps at 300 DIP ({narrowHeight} vs {wideHeight})");
                host.Width = double.NaN; window.UpdateLayout();
                Assert.AreEqual(0, OverflowAudit.Audit(host).Count, "nothing is clipped");
            }
            finally { try { window?.Close(); } catch (Exception) { } }
        });
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
