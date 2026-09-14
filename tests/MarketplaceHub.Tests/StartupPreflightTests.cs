using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class StartupPreflightTests
{
    [TestMethod]
    public void FreshEmptyProfileReportsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "preflight-ready-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = StartupPreflight.Run(root);
            Assert.AreEqual(StartupHealthStatus.Ready, report.Overall, string.Join(" | ", report.Checks));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CorruptCoreDatabaseRequiresRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "preflight-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "catalog.db"), "not a real sqlite file");
            var report = StartupPreflight.Run(root);
            Assert.AreEqual(StartupHealthStatus.RecoveryRequired, report.Overall);
            Assert.IsTrue(report.Checks.Any(c => c.Key == "core-database" && c.Status == StartupHealthStatus.RecoveryRequired));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void OverallStatusIsWorstOfAllChecks()
    {
        var checks = new[]
        {
            new StartupHealthCheck("a", StartupHealthStatus.Ready, ""),
            new StartupHealthCheck("b", StartupHealthStatus.Degraded, ""),
        };
        var report = new StartupHealthReport(checks);
        Assert.AreEqual(StartupHealthStatus.Degraded, report.Overall);
    }
}
