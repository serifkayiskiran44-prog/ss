using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #833 (DESIGN: Generic mapping required-field indicators). The Excel mapping dialog: required markers naming the
// alternative, masked samples, a missing count, a jump that focuses the first problem's combo, a confirm that is
// off until the mapping can import; long headers wrap and tooltip; and the page builds it from a real workbook.
[TestClass]
public sealed class ExcelMappingDialogTests
{
    static readonly string[] Headers = { "product_code", "title", "price", "mail", new string('x', 120) + "_uzun_başlık" };
    static string Sample(string header) => header switch { "product_code" => "ABC-1", "title" => "Kupa", "price" => "12,50", "mail" => "ali@example.com token=abc123SECRET", _ => "değer" };

    [TestMethod]
    public void MissingRequiredIsCountedNamedJumpedToAndBlocksTheConfirm()
    {
        RunSta(() =>
        {
            var view = ExcelMappingDialog.Build(null, Headers, Sample, new ExcelColumnMapping(new Dictionary<string, int> { ["Price"] = 3, ["Description"] = 4 }));
            Assert.IsNotNull(view.Table);
            Assert.AreEqual(3, view.Table!.MissingRequired, "SKU, barcode (one group) and name are all unmapped.");
            StringAssert.Contains(view.Summary.Text, "3 zorunlu eksik"); StringAssert.Contains(view.Summary.Text, "önce sorunları düzeltin");
            Assert.IsFalse(view.Confirm.IsEnabled, "A mapping that cannot import is never handed back.");
            Assert.AreEqual(Visibility.Visible, view.FirstProblem.Visibility);
            var sku = view.Combos["Sku"];
            StringAssert.Contains(System.Windows.Automation.AutomationProperties.GetName(sku), "zorunlu");
            var mailSample = Descendants(view.Window).OfType<TextBlock>().Select(t => t.Text).Single(t => t.Contains("örnek:") && t.Contains("[pii-email]"));
            Assert.IsFalse(mailSample.Contains("abc123SECRET") || mailSample.Contains("ali@"), "The first row's sample is masked -- the address is PII, the token a secret: " + mailSample);
            var required = Descendants(view.Window).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.IsTrue(required.Any(t => t.Contains("✱ zorunlu (veya barkod)")), "SKU says its alternative.");
            Assert.IsTrue(required.Any(t => t.StartsWith("isteğe bağlı · ")), "An optional field says so.");

            view.Window.Show(); Drain(view.Window);
            view.FirstProblem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(view.Window);
            Assert.IsTrue(sku.IsKeyboardFocusWithin || Keyboard.FocusedElement == sku, "The jump lands on the first problem's combo.");

            sku.SelectedItem = "product_code"; view.Combos["Name"].SelectedItem = "title"; Drain(view.Window);
            Assert.AreEqual(0, view.Table!.MissingRequired); Assert.IsTrue(view.Confirm.IsEnabled); StringAssert.Contains(view.Summary.Text, "eşleme hazır");
            Assert.AreEqual(Visibility.Collapsed, view.FirstProblem.Visibility);
            view.Combos["Brand"].SelectedItem = "title"; Drain(view.Window);
            Assert.AreEqual(2, view.Table!.Duplicates, "One header on two fields is a conflict on both."); Assert.IsFalse(view.Confirm.IsEnabled);
            view.Window.Close();
        });
    }

    [TestMethod]
    public void OptionalOnlyGapsAreCleanLongHeadersWrapAndCombosAreKeyboardReachable()
    {
        RunSta(() =>
        {
            var view = ExcelMappingDialog.Build(null, Headers, Sample, new ExcelColumnMapping(new Dictionary<string, int> { ["Sku"] = 1, ["Name"] = 2, ["Description"] = 5 }));
            Assert.AreEqual(0, view.Table!.MissingRequired); Assert.IsTrue(view.Confirm.IsEnabled);
            StringAssert.Contains(view.Summary.Text, "isteğe bağlı boş"); Assert.IsNull(view.Table.FirstProblemKey);
            Assert.IsTrue(view.Combos.Values.All(c => c.Focusable && KeyboardNavigation.GetIsTabStop(c)), "Every combo is keyboard-reachable.");
            var labels = Descendants(view.Window).OfType<TextBlock>().Where(t => t.MaxWidth <= 220).ToList();
            Assert.IsTrue(labels.Count > 0 && labels.All(t => t.TextWrapping == TextWrapping.Wrap), "Field labels wrap instead of pushing the combo off the row.");
            var description = view.Combos["Description"];
            Assert.IsTrue(description.MaxWidth <= 230, "A 130-character header does not widen the combo.");
            StringAssert.Contains(description.ToolTip?.ToString() ?? "", "_uzun_başlık", "The full header is a tooltip.");
            Assert.IsTrue(view.Window.Width <= 620, "DIP-sized dialog: " + view.Window.Width);
            view.Window.Close();
        });
    }

    [TestMethod]
    public void ThePageBuildsTheDialogFromTheWorkbooksHeadersAndFirstRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlsmap-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "urunler.xlsx");
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Sayfa1");
                    sheet.Cell(1, 1).Value = "product_code"; sheet.Cell(1, 2).Value = "title"; sheet.Cell(1, 3).Value = "price"; sheet.Cell(1, 4).Value = "mail";
                    sheet.Cell(2, 1).Value = "ABC-1"; sheet.Cell(2, 2).Value = "Kupa"; sheet.Cell(2, 3).Value = 12.5; sheet.Cell(2, 4).Value = "ali@example.com token=abc123SECRET";
                    book.SaveAs(path);
                }
                var firstRow = CatalogExcel.FirstRow(path);
                Assert.AreEqual("ABC-1", firstRow["product_code"]); Assert.AreEqual("Kupa", firstRow["title"]);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                var view = (ExcelMappingDialogView)typeof(MainWindow).GetMethod("BuildExcelMappingDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { path, new ExcelColumnMapping(new Dictionary<string, int> { ["Sku"] = 1, ["Description"] = 4 }) })!;
                Assert.AreEqual(1, view.Table!.MissingRequired, "Only the name is still missing.");
                StringAssert.Contains(view.Summary.Text, "1 zorunlu eksik");
                Assert.AreEqual("Name", view.Table.FirstProblemKey);
                var sku = view.Table.Rows.Single(r => r.Key == "Sku"); Assert.AreEqual("ABC-1", sku.Sample, "The sample comes from the workbook's first data row.");
                var mail = view.Table.Rows.Single(r => r.Key == "Description"); Assert.IsFalse(mail.Sample.Contains("abc123SECRET") || mail.Sample.Contains("ali@"), mail.Sample); StringAssert.Contains(mail.Sample, "[pii-email]");
                Assert.IsFalse(view.Confirm.IsEnabled);
                view.Combos["Name"].SelectedItem = "title"; Drain(window);
                Assert.IsTrue(view.Confirm.IsEnabled);
                view.Confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.IsNotNull(view.Result); Assert.AreEqual(2, view.Result!.Columns["Name"]); Assert.AreEqual(1, view.Result.Columns["Sku"]); Assert.AreEqual(4, view.Result.Columns["Description"]);
            }
            finally
            {
                try { window?.Close(); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var g in Descendants(d)) yield return g;
        }
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
