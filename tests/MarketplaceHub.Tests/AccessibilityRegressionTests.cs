using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #888 (TEST: WPF accessibility regression suite). The audit reads a screen the way a keyboard or screen-reader user
// meets it: every input announces a name, every enabled unit is a Tab stop the cycle reaches and comes back from,
// every focusable input keeps its focus visual, every disabled input explains itself. It runs on the product,
// order, import, channel and settings screens and on two dialogs over a synthetic fixture, and the high-contrast
// variants of the shared owners render on a real screen with the desktop state pinned.
[TestClass]
public sealed class AccessibilityRegressionTests
{
    [TestMethod]
    public void AuditNamesTheFourDefectsAndPassesACleanForm()
    {
        RunSta(() =>
        {
            var clean = new StackPanel();
            clean.Children.Add(new Button { Content = "Kaydet" });
            var named = new TextBox(); AutomationProperties.SetName(named, "Ürün adı"); clean.Children.Add(named);
            clean.Children.Add(new CheckBox { Content = "Aktif" });
            var combo = new ComboBox { ItemsSource = new[] { "TRY", "USD" }, SelectedIndex = 0 }; AutomationProperties.SetName(combo, "Para birimi"); clean.Children.Add(combo);
            var explained = new Button { Content = "Sil" }; CommandState.Apply(explained, DisabledReason.Selection("Önce satır seçin.")); clean.Children.Add(explained);
            var iconOnly = new Button { Content = "✕" }; AutomationProperties.SetName(iconOnly, "Bildirimi kapat"); clean.Children.Add(iconOnly);
            clean.Children.Add(new DataGrid { ItemsSource = new[] { new { A = 1 }, new { A = 2 } }, AutoGenerateColumns = true });

            var dirty = new StackPanel();
            dirty.Children.Add(new Button { Content = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10 } });
            var skipped = new TextBox { IsTabStop = false }; AutomationProperties.SetName(skipped, "Atlanan"); dirty.Children.Add(skipped);
            var silent = new Button { Content = "Uygula", IsEnabled = false }; dirty.Children.Add(silent);
            var surface = FocusStyles.MakeFocusable(new Border { Height = 20, Background = Brushes.LightGray }); surface.FocusVisualStyle = null; AutomationProperties.SetName(surface, "Satır"); dirty.Children.Add(surface);

            var root = new StackPanel(); root.Children.Add(clean); root.Children.Add(dirty);
            var window = new Window { Content = root, Width = 500, Height = 500, Left = -4000, Top = -4000 };
            // The app's control styles (the token file gives every input the keyboard focus ring, #861); a bare theme text box has none.
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); window.Activate(); Drain(window);
                Assert.AreEqual("", string.Join("; ", AccessibilityAudit.Audit(clean)), "a clean form has no finding");
                var findings = AccessibilityAudit.Audit(dirty);
                CollectionAssert.AreEquivalent(new[] { AccessibilityIssue.Unnamed, AccessibilityIssue.Unreachable, AccessibilityIssue.DisabledWithoutReason, AccessibilityIssue.NoFocusVisual }, findings.Select(f => f.Issue).ToArray(), string.Join("; ", findings));
                StringAssert.Contains(findings.Single(f => f.Issue == AccessibilityIssue.Unreachable).Owner, "Atlanan");
                StringAssert.Contains(findings.Single(f => f.Issue == AccessibilityIssue.DisabledWithoutReason).Owner, "Uygula");

                // Explaining the disabled command clears its finding; so does a tooltip that shows while disabled.
                CommandState.Apply(silent, DisabledReason.Busy("İşlem sürüyor."));
                Assert.AreEqual(0, AccessibilityAudit.DisabledReasons(dirty).Count);
                var tipped = new Button { Content = "Gönder", IsEnabled = false, ToolTip = "Önce kaynağı okuyun." }; ToolTipService.SetShowOnDisabled(tipped, true); dirty.Children.Add(tipped); Drain(window);
                Assert.AreEqual(0, AccessibilityAudit.DisabledReasons(dirty).Count);

                // The walk comes back to its start and visits the form in document order; the name comes from the automation name, the label, the content or the tooltip.
                var walk = AccessibilityAudit.Walk(clean);
                Assert.IsTrue(walk.Closed, $"{walk.Stops.Count} stops");
                Assert.IsTrue(walk.Stops.IndexOf(named) < walk.Stops.IndexOf(combo), "document order");
                Assert.AreEqual("Ürün adı", AccessibilityAudit.AccessibleName(named)); Assert.AreEqual("Kaydet", AccessibilityAudit.AccessibleName((Button)clean.Children[0])); Assert.AreEqual("Bildirimi kapat", AccessibilityAudit.AccessibleName(iconOnly));
                var labelled = new TextBox(); var label = new TextBlock { Text = "Açıklama" }; AutomationProperties.SetLabeledBy(labelled, label);
                Assert.AreEqual("Açıklama", AccessibilityAudit.AccessibleName(labelled));
                Assert.AreEqual("", AccessibilityAudit.AccessibleName(new TextBox()));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void MainScreensAndDialogsAreReachableNamedAndExplainedAndHighContrastRendersSystemColours()
    {
        var root = Path.Combine(Path.GetTempPath(), "a11y-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null; Window dialog = null;
            var report = new StringBuilder();
            try
            {
                Directory.CreateDirectory(root);
                var catalog = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Tedarikçi beslemesi", Location = Path.Combine(root, "feed.xml"), ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title" } };
                catalog.SaveSource(source);
                catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-0001", Name = "İğne oyası şal", Price = 99, Stock = 3, Currency = "TRY" }, new CatalogProduct { SourceId = source.Id, Sku = "SKU-0002", Name = "Kısa ürün", Price = 10, Stock = 5, Currency = "EUR" } });
                new OrdersStore(root).SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1), Total = 42, Currency = "USD", Items = [new() { Sku = "SKU-0002", Title = "Kısa ürün", Quantity = 1 }] });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show(); window.Activate(); Drain(window);
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var walked = new Dictionary<string, int>();
                void Check(string route, FrameworkElement content)
                {
                    var walk = AccessibilityAudit.Walk(content); walked[route] = walk.Stops.Count;
                    foreach (var finding in AccessibilityAudit.Audit(content, walk)) report.AppendLine($"{route}: {finding}");
                }

                // Products, with a product selected so the editor is live.
                Navigate(window, "products"); Drain(window);
                var products = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                products.SelectedIndex = 0; Drain(window);
                Check("products", (FrameworkElement)routes["products"].Content);

                // Orders.
                Navigate(window, "orders"); Drain(window);
                Check("orders", (FrameworkElement)routes["orders"].Content);

                // Import, with the source selected so the form and the stepper are live.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                Check("xml", (FrameworkElement)routes["xml"].Content);

                // A channel page: its product plans and its connection form.
                Navigate(window, "trendyol"); Drain(window);
                var channelTabs = (TabControl)routes["trendyol"].Content;
                Check("trendyol/products", channelTabs);
                channelTabs.SelectedIndex = 1; Drain(window);
                Check("trendyol/connection", channelTabs);

                // Settings.
                Navigate(window, "settings"); Drain(window);
                Check("settings", (FrameworkElement)routes["settings"].Content);
                Assert.IsTrue(walked.Values.All(n => n > 5), "every screen has keyboard stops: " + string.Join(", ", walked.Select(kv => kv.Key + "=" + kv.Value)));

                // Dialogs: the shortcut reference and a destructive confirmation, each a window of its own.
                dialog = KeyboardShortcutReference.Build(window); dialog.Show(); dialog.Activate(); Drain(dialog);
                foreach (var finding in AccessibilityAudit.Audit((FrameworkElement)dialog.Content)) report.AppendLine($"shortcut reference: {finding}");
                dialog.Close(); dialog = null; window.Activate(); Drain(window);
                dialog = DialogShell.Create(window, "Ürünleri sil", DialogShell.Message("2 ürün havuzdan silinecek; kanal planları da kaldırılır."), new[] { new DialogShell.Action("Sil", IsPrimary: true), new DialogShell.Action("Vazgeç", IsCancel: true) }, 420, 220, destructive: true);
                dialog.Show(); dialog.Activate(); Drain(dialog);
                var dialogWalk = AccessibilityAudit.Walk((FrameworkElement)dialog.Content);
                foreach (var finding in AccessibilityAudit.Audit((FrameworkElement)dialog.Content, dialogWalk)) report.AppendLine($"confirmation: {finding}");
                Assert.IsTrue(dialogWalk.Closed && dialogWalk.Stops.Count == 2, "the confirmation's Tab cycle is its two buttons: " + dialogWalk.Stops.Count);
                var cancel = Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(b => b.IsCancel);
                Assert.IsTrue(cancel.IsDefault, "a destructive dialog defaults to the safe button");
                dialog.Close(); dialog = null; window.Activate(); Drain(window);

                Assert.AreEqual("", report.ToString(), "Accessibility findings:\n" + report);
                window.Close(); Drain(window); window = null;

                // High contrast, pinned: the empty state, the focus ring and the severity colours come from the system on a real screen.
                SeverityStyle.HighContrastOverride = true;
                var hcRoot = Path.Combine(root, "hc"); Directory.CreateDirectory(hcRoot);
                window = new MainWindow(hcRoot); window.Show(); window.Activate(); Drain(window);
                Navigate(window, "products"); Drain(window);
                var hcRoutes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var empty = Descendants((DependencyObject)hcRoutes["products"].Content).OfType<Border>().First(b => b.Tag as string == EmptyState.Tag && b.IsVisible);
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)empty.BorderBrush).Color, "the empty state's accent is the system highlight under high contrast");
                Assert.AreEqual(SystemColors.WindowColor, ((SolidColorBrush)empty.Background).Color);
                var ring = (ControlTemplate)FocusStyles.Create().Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
                var strokes = Descendants((DependencyObject)ring.LoadContent()).OfType<System.Windows.Shapes.Rectangle>().Select(r => r.Stroke).ToList();
                Assert.AreSame(SystemColors.WindowTextBrush, strokes[0]); Assert.AreSame(SystemColors.WindowBrush, strokes[1]);
                Assert.IsTrue(ContrastAudit.Audit(true).All(f => f.Passes || f.Kind == ContrastKind.System), string.Join("; ", ContrastAudit.Audit(true).Where(f => !f.Passes)));
                foreach (var finding in AccessibilityAudit.Audit((FrameworkElement)hcRoutes["products"].Content)) report.AppendLine($"high contrast products: {finding}");
                Assert.AreEqual("", report.ToString(), "Accessibility findings under high contrast:\n" + report);
            }
            finally
            {
                SeverityStyle.HighContrastOverride = null;
                try { dialog?.Close(); } catch (Exception) { }
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

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
