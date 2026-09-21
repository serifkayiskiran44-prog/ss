using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class TrendyolContextMenuTests
{
    [TestMethod]
    public void ProductGridOffersEntegraStyleMatchingActions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "trendyol-context-" + Guid.NewGuid().ToString("N"));
            try
            {
                var panel = new TrendyolWorkspacePanel(directory);
                var field = typeof(TrendyolWorkspacePanel).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var grid = (DataGrid)field.GetValue(panel)!;
                var labels = grid.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                CollectionAssert.Contains(labels, "Kategori eşleştir");
                CollectionAssert.Contains(labels, "Marka seç");
                CollectionAssert.Contains(labels, "Barkod / GTIN ile kategori ve marka bul");
                CollectionAssert.Contains(labels, "Barkodla eşleştir / oluştur");
            }
            catch (Exception ex) { failure = ex; }
            finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
