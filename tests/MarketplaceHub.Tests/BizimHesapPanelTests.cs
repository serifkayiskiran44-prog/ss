using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BizimHesapPanelTests
{
    [TestMethod]
    public void Product_create_ui_is_explicitly_single_item()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "bizimhesap-panel-" + Guid.NewGuid().ToString("N"));
            try
            {
                var panel = BizimHesapPanel.Create(directory);
                var labels = Walk(panel).OfType<Button>().Select(button => button.Content as string).ToArray();

                CollectionAssert.Contains(labels, "İlk yeni ürünü seç");
                CollectionAssert.Contains(labels, "Seçili ürünü oluştur / fiyat güncelle");
                CollectionAssert.DoesNotContain(labels, "Seçilen yeni ürünleri BizimHesap'a gönder");
            }
            catch (Exception e) { failure = e; }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
