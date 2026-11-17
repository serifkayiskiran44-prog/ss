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
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaPanelTests
{
    [TestMethod]
    public void AccountOpensSpecializedTurkishWorkspaceWithoutRenderingSecret() => InSta(root =>
    {
        var connection = new MarketplaceConnectionStore(root).Save("hepsiburada", "merchant-a", "Hepsiburada A", true);
        new MarketplaceCredentialVault(root).Save(connection.Id, connection.Channel, connection.ShopId,
            new HepsiburadaCredentials("merchant-a", "test-secret", HepsiburadaEnvironment.Production, "MonoBridge/1"));

        using var host = MarketplaceWorkspaceHost.Create(connection.Id, root);
        Assert.IsInstanceOfType<HepsiburadaWorkspacePanel>(host.Workspace);
        var controls = Walk(host).ToArray();
        Assert.IsNotNull(controls.OfType<DataGrid>().SingleOrDefault(x => x.Name == "MarketplaceShopProducts"));
        Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(x => x.Name == "MarketplaceOpenProductCard"));
        Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(x => x.Name == "HepsiburadaReadProducts"));
        Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(x => x.Name == "HepsiburadaCreateDispatchPreview"));
        Assert.IsNotNull(controls.OfType<ComboBox>().SingleOrDefault(x => x.Name == "HepsiburadaPreviewOperation"));
        Assert.IsFalse(VisibleText(host).Contains("test-secret", StringComparison.Ordinal));
        CollectionAssert.IsSubsetOf(new[] { "Hepsiburada kontrol", "Ayarlar", "Rekabet analizi", "İşlem geçmişi" },
            controls.OfType<TabItem>().Select(x => x.Header?.ToString() ?? "").ToArray());
    });

    [TestMethod]
    public void CatalogAndAdapterExposeOnlyImplementedReadPhaseCapabilities() =>
        InSta(root =>
        {
            var definition = MarketplaceConnectionCatalog.Get("hepsiburada");
            var adapter = MarketplaceAdapterRegistry.CreateDefault(root).Get("hepsiburada");
            Assert.IsFalse(definition.LiveApiBlocked);
            Assert.IsTrue(adapter.Capabilities.Supports(MarketplaceOperation.ProductsRead));
            Assert.IsTrue(adapter.Capabilities.Supports(MarketplaceOperation.ProductManagement));
            Assert.IsFalse(adapter.Capabilities.Supports(MarketplaceOperation.StockWrite));
            Assert.IsFalse(adapter.Capabilities.Supports(MarketplaceOperation.PriceWrite));
        });

    static string VisibleText(DependencyObject root) => string.Join("\n", Walk(root).Select(item => item switch
    {
        TextBlock text => text.Text,
        ContentControl content => content.Content?.ToString() ?? "",
        _ => ""
    }));

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Walk(child)) yield return item;
    }

    static void InSta(Action<string> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "hepsiburada-panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            catch (Exception error) { failure = error; }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
