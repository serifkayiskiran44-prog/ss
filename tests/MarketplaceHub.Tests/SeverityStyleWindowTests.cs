using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #817 in the real product card: a product with a blocking finding and a warning shows both with their own
// glyph and word (not only colour), the headline leads with the blocking count, and the one call to action is a
// focusable button that belongs to the blocking finding.
[TestClass]
public sealed class SeverityStyleWindowTests
{
    [TestMethod]
    public void MixedFindingsShowDistinctBadgesAndOneFocusableCallToActionForTheBlockingOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "sev-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "MIX", Name = "Karışık", Price = 10, Stock = 4, Currency = "TRY" } });
                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "products", true });
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Drain(window);
                grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(p => p.Sku == "MIX"); Drain(window);
                var edited = (CatalogProduct)typeof(MainWindow).GetField("edit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                edited.Name = ""; edited.Description = "kısa";
                typeof(MainWindow).GetMethod("ShowProductValidation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { edited });
                Drain(window);
                var panel = (StackPanel)typeof(MainWindow).GetField("productValidationPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(Visibility.Visible, panel.Visibility);
                var texts = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.IsTrue(texts.Any(t => t.StartsWith("✖ Hata · ", StringComparison.Ordinal)), "A blocking finding carries its glyph and its word: " + string.Join(" | ", texts));
                var headline = texts.First();
                StringAssert.Contains(headline, "engel", "The headline leads with what blocks the save.");
                var cta = Descendants(panel).OfType<Button>().Single();
                Assert.AreEqual("İlk engele git", cta.Content?.ToString(), "The single call to action belongs to the blocking finding.");
                Assert.IsTrue(cta.Focusable, "The call to action is reachable from the keyboard.");
                edited.Name = "Karışık";
                typeof(MainWindow).GetMethod("ShowProductValidation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { edited });
                Drain(window);
                var after = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.IsFalse(after.Any(t => t.StartsWith("✖ Hata", StringComparison.Ordinal)), "With the blocker fixed, no blocking badge remains.");
                Assert.IsFalse(Descendants(panel).OfType<Button>().Any(b => b.Content?.ToString() == "İlk engele git"), "No blocker, no blocking call to action.");
                // Leave the draft exactly as it was loaded: a dirty draft makes Close() ask about unsaved changes, and a
                // modal question in a test host waits forever.
                edited.Description = "";
                typeof(MainWindow).GetMethod("ShowProductValidation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { edited });
                Drain(window);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        if (node is Panel p) foreach (UIElement child in p.Children) { yield return child; foreach (var d in Descendants(child)) yield return d; }
        else if (node is Decorator dec && dec.Child is not null) { yield return dec.Child; foreach (var d in Descendants(dec.Child)) yield return d; }
        else if (node is ContentControl cc && cc.Content is DependencyObject content) { yield return content; foreach (var d in Descendants(content)) yield return d; }
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
