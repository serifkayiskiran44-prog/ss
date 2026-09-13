using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #822 in the real XML page: with a saved source and no XML read yet, the strip shows the source done, the mapping
// blocked with its reason, preview and apply unreachable, every chip a focusable button with a spoken state.
[TestClass]
public sealed class ImportStepperWindowTests
{
    [TestMethod]
    public void TheStripReflectsTheRealFlowStateAndItsChipsAreKeyboardReachable()
    {
        var root = Path.Combine(Path.GetTempPath(), "stepper-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            try
            {
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = "https://example.invalid/feed.xml" });
                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);

                var strip = (StackPanel)typeof(MainWindow).GetField("importStepper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var chips = strip.Children.OfType<Button>().ToList();
                Assert.AreEqual(ImportStepper.Stages.Count, chips.Count, "One chip per stage.");
                Assert.IsTrue(chips.All(c => c.Focusable), "Every chip is keyboard-reachable.");
                Assert.IsTrue(chips.All(c => AutomationProperties.GetName(c).Contains(':')), "Every chip speaks its state.");

                Button Chip(ImportStage stage) => chips.Single(c => (ImportStage)c.Tag == stage);
                StringAssert.StartsWith(Chip(ImportStage.Source).Content.ToString(), "✔", "A chosen, saved source is done.");
                StringAssert.StartsWith(Chip(ImportStage.Mapping).Content.ToString(), "✖", "No XML read yet: mapping is blocked.");
                StringAssert.Contains(Chip(ImportStage.Mapping).ToolTip.ToString(), "okunmadı");
                Assert.IsFalse(Chip(ImportStage.Preview).IsEnabled, "No read, no preview to jump to.");
                Assert.IsFalse(Chip(ImportStage.Apply).IsEnabled, "No preview, no apply.");
                StringAssert.Contains(Chip(ImportStage.Apply).Content.ToString(), "yerel", "Apply says it writes the local pool.");
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

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
