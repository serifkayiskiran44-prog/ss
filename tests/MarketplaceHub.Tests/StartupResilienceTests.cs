using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class StartupResilienceTests
{
    [TestMethod]
    public void RepeatedFailedLaunchesEnterRecoveryModeAfterThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < StartupCrashGuard.RecoveryThreshold; i++)
            {
                var guard = new StartupCrashGuard(root);
                Assert.IsFalse(guard.RecoveryModeRequired, $"launch #{i + 1} should not be in recovery mode yet");
            }
            var final = new StartupCrashGuard(root);
            Assert.IsTrue(final.RecoveryModeRequired);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SuccessfulLaunchResetsFailureCounter()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new StartupCrashGuard(root);
            _ = new StartupCrashGuard(root);
            var third = new StartupCrashGuard(root);
            third.MarkReachedUi();

            var fourth = new StartupCrashGuard(root);
            Assert.AreEqual(0, fourth.ConsecutiveFailures);
            Assert.IsFalse(fourth.RecoveryModeRequired);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
