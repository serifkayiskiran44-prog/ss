using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #863 on the real main window: the product card's inputs, the XML source form's inputs, the locale settings form's
// inputs and the report setup's date pickers all stand at least the control height; every button and check box on
// those screens is at least the hit-target minimum; a long Turkish value keeps a single-line input's height; the
// keyboard walks the locale form's inputs in order.
[TestClass]
public sealed class FormControlStylesUiTests
{
    [TestMethod]
    public void ProductSourceAndSettingsFormsShareTheControlHeightsAndHitTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "form-controls-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = "https://example.invalid/feed.xml" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10, Currency = "TRY" } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var content = (TabControl)window.FindName("ModuleTabs");

                void CheckScreen(string route, Func<IEnumerable<FrameworkElement>> scope)
                {
                    // A control's own template parts (the date picker's inner text box) are the control's business; the control is what must stand at the height.
                    var inputs = scope().Where(e => e is TextBox or PasswordBox or ComboBox or DatePicker).Where(e => e is not System.Windows.Controls.Primitives.DatePickerTextBox && e.TemplatedParent is null).Where(e => e.IsVisible && e.ActualWidth > 0).ToList();
                    Assert.IsTrue(inputs.Count > 0, $"{route}: inputs on screen");
                    var short_ = inputs.Where(e => e.ActualHeight < DesignTokens.ControlMinHeight - 0.5).Select(e => $"{e.GetType().Name} {e.ActualHeight:0}").ToList();
                    Assert.AreEqual(0, short_.Count, $"{route}: inputs under the control height: {string.Join(", ", short_)}");
                    var targets = scope().Where(e => e is Button or CheckBox).Where(e => e.TemplatedParent is null).Where(e => e.IsVisible && e.ActualWidth > 0).ToList();
                    var small = targets.Where(e => e.ActualHeight < DesignTokens.HitTargetMinSize - 0.5 || e.ActualWidth < DesignTokens.HitTargetMinSize - 0.5).Select(e => $"{e.GetType().Name} '{(e as ContentControl)?.Content}' {e.ActualWidth:0}x{e.ActualHeight:0}").ToList();
                    Assert.AreEqual(0, small.Count, $"{route}: targets under the hit-target minimum: {string.Join(", ", small)}");
                }

                // The product card.
                Navigate(window, "products"); Drain(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(); Drain(window);
                var editor = (StackPanel)typeof(MainWindow).GetField("productEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                CheckScreen("products", () => Descendants(editor).OfType<FrameworkElement>());

                // The XML source form.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var sourceGeneral = (StackPanel)typeof(MainWindow).GetField("sourceGeneral", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                CheckScreen("xml", () => Descendants(sourceGeneral).OfType<FrameworkElement>());

                // The locale settings form, with the keyboard walking its inputs in order.
                Navigate(window, "locale-settings"); Drain(window);
                var localeInputs = Descendants(content).OfType<FrameworkElement>().Where(e => e is TextBox or ComboBox).Where(e => e.IsVisible).ToList();
                CheckScreen("locale-settings", () => Descendants(content).OfType<FrameworkElement>());
                var first = localeInputs.OfType<TextBox>().First(); first.Focus(); Drain(window);
                var order = new List<FrameworkElement> { first };
                for (var i = 0; i < 5; i++) { ((FrameworkElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); Drain(window); order.Add((FrameworkElement)Keyboard.FocusedElement); }
                Assert.IsTrue(order.Distinct().Count() == order.Count, "Tab visits a new control each time.");
                Assert.IsTrue(order.Skip(1).Take(5).All(e => e is TextBox or ComboBox or Button), "The form's inputs and buttons are the keyboard stops, in order.");

                // A long Turkish value keeps a single-line input's height.
                var height = first.ActualHeight; first.Text = string.Concat(Enumerable.Repeat("Şüpheli işlem çözümü ", 20)); Drain(window);
                Assert.AreEqual(height, first.ActualHeight, 0.5);

                // The report setup's date pickers.
                Navigate(window, "reports"); Drain(window);
                var open = Descendants(content).OfType<Button>().FirstOrDefault(b => b.Content as string == "Aç" || (b.Content as string ?? "").StartsWith("Ayarla", StringComparison.Ordinal));
                if (open is not null) { open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window); }
                var pickers = Descendants(content).OfType<DatePicker>().Where(d => d.IsVisible).ToList();
                if (pickers.Count > 0) Assert.IsTrue(pickers.All(d => d.ActualHeight >= DesignTokens.ControlMinHeight - 0.5), string.Join(",", pickers.Select(d => d.ActualHeight)));
            }
            finally
            {
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
