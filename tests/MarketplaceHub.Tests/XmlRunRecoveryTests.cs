using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// A run that was still Running when the app was killed (crash, power loss) must not block its XML source for good.
[TestClass]
public sealed class XmlRunRecoveryTests
{
    string root = "";
    XmlRunStore store = null!;

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "xmlrun-recovery-" + Guid.NewGuid().ToString("N"));
        store = new XmlRunStore(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public void AnAbandonedRunIsFailedAndNoLongerBlocksItsSource()
    {
        var abandoned = store.Start("src1");
        Assert.ThrowsException<InvalidOperationException>(() => store.Start("src1"));

        var recovered = store.RecoverAbandonedRunning(TimeSpan.Zero);

        Assert.AreEqual(1, recovered);
        var row = store.List("src1").Single(x => x.Id == abandoned);
        Assert.AreEqual("Failed", row.Status);
        Assert.IsNotNull(row.FinishedUtc);
        StringAssert.Contains(row.Error, "yarıda");
        var next = store.Start("src1");
        store.Complete(next, new ImportSummary(1, 0, 0));
        Assert.ThrowsException<InvalidOperationException>(() => store.Complete(abandoned, new ImportSummary(9, 9, 9)), "A recovered run can no longer report success.");
    }

    [TestMethod]
    public void ARunYoungerThanTheGivenAgeIsLeftRunning()
    {
        store.Start("src1");

        Assert.AreEqual(0, store.RecoverAbandonedRunning(TimeSpan.FromHours(2)));

        Assert.ThrowsException<InvalidOperationException>(() => store.Start("src1"));
        Assert.AreEqual("Running", store.List("src1").Single().Status);
    }

    [TestMethod]
    public void StartHealsARunOlderThanTheCeilingButOnlyForStaleRows()
    {
        var stale = store.Start("src1");
        var live = store.Start("src2");
        Age(stale, XmlRunStore.StaleRunCeiling + TimeSpan.FromMinutes(1));

        var next = store.Start("src1");

        Assert.AreNotEqual(stale, next);
        Assert.AreEqual("Failed", store.List("src1").Single(x => x.Id == stale).Status);
        Assert.AreEqual("Running", store.List("src2").Single(x => x.Id == live).Status);
        Assert.ThrowsException<InvalidOperationException>(() => store.Start("src2"));
    }

    [TestMethod]
    public void ARunningRowWithAnUnreadableStartTimeIsTreatedAsAbandoned()
    {
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO XmlRuns(Id,SourceId,Status,StartedUtc) VALUES('bad','src1','Running','not-a-date')";
            cmd.ExecuteNonQuery();
        }

        Assert.AreEqual(1, store.RecoverAbandonedRunning(TimeSpan.FromHours(2)));

        store.Start("src1");
    }

    void Age(string id, TimeSpan age)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE XmlRuns SET StartedUtc=$started WHERE Id=$id";
        cmd.Parameters.AddWithValue("$started", (DateTime.UtcNow - age).ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", id);
        Assert.AreEqual(1, cmd.ExecuteNonQuery());
    }
}
