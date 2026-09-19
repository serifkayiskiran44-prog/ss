using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class SingleInstanceGuardTests
{
    [TestMethod]
    public void EquivalentNormalizedDataDirectoriesCannotBothBeAcquiredAndSignalTheOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "single-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var activated = new ManualResetEventSlim();
        using var first = SingleInstanceGuard.TryAcquire(root + Path.DirectorySeparatorChar, activated.Set);
        Assert.IsNotNull(first);

        using var second = SingleInstanceGuard.TryAcquire(Path.Combine(root, "."));

        Assert.IsNull(second);
        Assert.IsTrue(activated.Wait(TimeSpan.FromSeconds(2)), "The second launch must activate the existing process.");
        Assert.IsFalse(first.InstanceName.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(first.InstanceName.Length <= SingleInstanceGuard.MaxInstanceNameLength);
    }

    [TestMethod]
    public void DifferentDataDirectoriesHaveIndependentGuards()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "single-instance-a-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "single-instance-b-" + Guid.NewGuid().ToString("N"));
        using var first = SingleInstanceGuard.TryAcquire(firstRoot);
        using var second = SingleInstanceGuard.TryAcquire(secondRoot);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first.InstanceName, second.InstanceName);
    }

    [TestMethod]
    public void DisposeReleasesTheGuardForANewLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "single-instance-release-" + Guid.NewGuid().ToString("N"));
        var first = SingleInstanceGuard.TryAcquire(root);
        Assert.IsNotNull(first);
        first.Dispose();

        using var reopened = SingleInstanceGuard.TryAcquire(root);
        Assert.IsNotNull(reopened);
    }
}
