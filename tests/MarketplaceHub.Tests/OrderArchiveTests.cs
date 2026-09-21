using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderArchiveTests
{
    [TestMethod]
    public void ArchiveAndRestoreMovesExactOrderWithoutDataLoss()
    {
        var directory=Path.Combine(Path.GetTempPath(),"monobridge-archive-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new OrdersStore(directory);
            var order=new OrderSnapshot{Marketplace="Trendyol",ShopId="42",OrderId="100",CustomerName="Ayşe",Items=[new(){Title="Mama",Sku="SKU-1",Quantity=2}]};
            store.SaveManual(order);
            store.Archive("Trendyol","42","100","test");
            Assert.AreEqual(0,store.ReadAll().Count);
            Assert.AreEqual("Ayşe",store.ReadArchived().Single().CustomerName);
            store.SaveBatch([order]);
            Assert.AreEqual(0,store.ReadAll().Count,"API yenilemesi arşivlenmiş siparişi etkin listeye geri koymamalı.");
            store.Restore("Trendyol","42","100");
            Assert.AreEqual("Ayşe",store.ReadAll().Single().CustomerName);
            Assert.AreEqual(0,store.ReadArchived().Count);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
