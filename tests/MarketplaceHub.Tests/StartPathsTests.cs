using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Simulates launching the EXE from an unrelated working directory (Desktop shortcut,
/// Start Menu, or a shell with a different CWD): resource/template resolution and the
/// local data directory must depend only on AppContext.BaseDirectory / LocalAppData,
/// never on Environment.CurrentDirectory.
[TestClass]
public sealed class StartPathsTests
{
    [TestMethod]
    public void TemplateAndDataPathsAreUnaffectedByWorkingDirectory()
    {
        var originalCwd = Environment.CurrentDirectory;
        var unrelatedCwd = Path.Combine(Path.GetTempPath(), "start-paths-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelatedCwd);
        var dataRoot = Path.Combine(Path.GetTempPath(), "start-paths-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baselineReport = StartupPreflight.Run(dataRoot + "-baseline");
            Environment.CurrentDirectory = unrelatedCwd;

            var report = StartupPreflight.Run(dataRoot);

            Assert.AreEqual(baselineReport.Overall, report.Overall, string.Join(" | ", report.Checks));
            var templatesCheck = report.Checks.Single(c => c.Key == "templates");
            Assert.AreNotEqual(StartupHealthStatus.Blocked, templatesCheck.Status, templatesCheck.Detail);

            // The data directory itself must land where requested, not under the
            // unrelated CWD - proving no relative-path fallback snuck in.
            Assert.IsTrue(Directory.Exists(dataRoot));
            Assert.IsFalse(File.Exists(Path.Combine(unrelatedCwd, "catalog.db")));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(unrelatedCwd)) Directory.Delete(unrelatedCwd, true);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
            if (Directory.Exists(dataRoot + "-baseline")) Directory.Delete(dataRoot + "-baseline", true);
        }
    }
}
