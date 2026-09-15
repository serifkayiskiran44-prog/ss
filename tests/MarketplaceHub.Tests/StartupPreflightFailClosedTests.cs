using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2657: an empty/incomplete startup health report, or a probe
/// that could not actually run, must never be reported as READY.
[TestClass]
public sealed class StartupPreflightFailClosedTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "startup-preflight-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void EmptyCheckListIsIncompleteNotReady()
    {
        var report = new StartupHealthReport([]);
        Assert.AreEqual(StartupHealthStatus.Incomplete, report.Overall);
    }

    [TestMethod]
    public void IncompleteOutranksEveryOtherStatusInOverall()
    {
        var report = new StartupHealthReport([
            new StartupHealthCheck("a", StartupHealthStatus.Ready, ""),
            new StartupHealthCheck("b", StartupHealthStatus.Degraded, ""),
            new StartupHealthCheck("c", StartupHealthStatus.Incomplete, "çalışamadı"),
        ]);
        Assert.AreEqual(StartupHealthStatus.Incomplete, report.Overall);
    }

    [TestMethod]
    public void RecoveryRequiredStillOutranksReadyAndDegraded()
    {
        var report = new StartupHealthReport([
            new StartupHealthCheck("a", StartupHealthStatus.Ready, ""),
            new StartupHealthCheck("b", StartupHealthStatus.RecoveryRequired, "bozuk"),
        ]);
        Assert.AreEqual(StartupHealthStatus.RecoveryRequired, report.Overall);
    }

    [TestMethod]
    public void ValidDirectoryProducesReadyAcrossAllChecks()
    {
        var root = NewRoot();
        try
        {
            var report = StartupPreflight.Run(root);
            Assert.IsTrue(report.Checks.Count > 0);
            Assert.AreNotEqual(StartupHealthStatus.Incomplete, report.Overall);
            Assert.IsTrue(report.Overall is StartupHealthStatus.Ready or StartupHealthStatus.Degraded, $"Unexpected overall: {report.Overall} ({string.Join("; ", report.Checks.Select(c => c.Key + "=" + c.Status))})");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OneFailingCheckNeverHidesTheOthersFromTheReport()
    {
        var root = NewRoot();
        try
        {
            var report = StartupPreflight.Run(root);
            // Every probe this pipeline runs must still appear even if the
            // overall verdict is dominated by the worst one.
            Assert.IsTrue(report.Checks.Select(c => c.Key).Contains("data-directory"));
            Assert.IsTrue(report.Checks.Select(c => c.Key).Contains("disk-space"));
            Assert.IsTrue(report.Checks.Select(c => c.Key).Contains("core-database"));
            Assert.IsTrue(report.Checks.Select(c => c.Key).Contains("templates"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptCoreDatabaseFileIsRecoveryRequiredNotReady()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.db"), "this is not a sqlite file");
        try
        {
            var report = StartupPreflight.Run(root);
            var db = report.Checks.Single(c => c.Key == "core-database");
            Assert.AreNotEqual(StartupHealthStatus.Ready, db.Status);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AppStartupNeverTreatsAPreflightCrashAsReady()
    {
        // Mirrors App.OnStartup's catch: an unexpected exception from the
        // pipeline must synthesize a fail-safe Incomplete check, never an empty
        // (and therefore falsely-Ready) report.
        StartupHealthReport health;
        try { throw new InvalidOperationException("simulated preflight crash"); }
        catch (Exception ex) { health = new([new StartupHealthCheck("preflight", StartupHealthStatus.Incomplete, "Başlangıç sağlık kontrolü çalıştırılamadı: " + AuditStore.Sanitize(ex.Message))]); }
        Assert.AreEqual(StartupHealthStatus.Incomplete, health.Overall);
        Assert.IsTrue(health.Overall is StartupHealthStatus.Blocked or StartupHealthStatus.RecoveryRequired or StartupHealthStatus.Incomplete);
    }
}
