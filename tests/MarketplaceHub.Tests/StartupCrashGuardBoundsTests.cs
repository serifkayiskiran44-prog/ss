using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2647: StartupCrashGuard must never read an unbounded marker
/// file, must never silently treat a corrupt/oversized/malformed marker as
/// "0 failures" (which would disable crash-loop protection), and a write
/// failure must stay visible rather than being swallowed.
[TestClass]
public sealed class StartupCrashGuardBoundsTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "crash-guard-" + Guid.NewGuid().ToString("N"));
    static string MarkerPath(string root) => Path.Combine(root, "startup-crash-count.txt");

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingMarkerStartsAtZero() => WithRoot(root =>
    {
        var guard = new StartupCrashGuard(root);
        Assert.AreEqual(0, guard.ConsecutiveFailures);
        Assert.IsFalse(guard.MarkerCorrupt);
    });

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("2")]
    [DataRow("3")]
    public void ValidCountsRoundTrip(string value) => WithRoot(root =>
    {
        File.WriteAllText(MarkerPath(root), value);
        var guard = new StartupCrashGuard(root);
        Assert.AreEqual(int.Parse(value), guard.ConsecutiveFailures);
        Assert.IsFalse(guard.MarkerCorrupt);
    });

    [DataTestMethod]
    [DataRow("not-a-number")]
    [DataRow("-1")]
    [DataRow("99999999999999999999")] // overflow
    [DataRow("   ")]
    [DataRow("3.5")]
    [DataRow("+3")]
    public void MalformedMarkerFailsClosedNotToZero(string value) => WithRoot(root =>
    {
        File.WriteAllText(MarkerPath(root), value);
        var guard = new StartupCrashGuard(root);
        Assert.IsTrue(guard.MarkerCorrupt, "A malformed marker must be flagged, not silently treated as healthy.");
        Assert.IsTrue(guard.RecoveryModeRequired, "A malformed marker must fail closed into recovery mode, never silently reset the crash counter to 0.");
    });

    [TestMethod]
    public void OversizedMarkerFileIsNeverFullyReadIntoMemory() => WithRoot(root =>
    {
        // Far larger than any legitimate small integer marker could ever be.
        File.WriteAllText(MarkerPath(root), new string('9', 10_000_000));
        var guard = new StartupCrashGuard(root);
        Assert.IsTrue(guard.MarkerCorrupt);
        Assert.IsTrue(guard.RecoveryModeRequired);
    });

    [TestMethod]
    public void SuccessfulResetAfterCorruptionStartsCleanAgain() => WithRoot(root =>
    {
        File.WriteAllText(MarkerPath(root), "garbage");
        var guard = new StartupCrashGuard(root);
        Assert.IsTrue(guard.RecoveryModeRequired);
        guard.MarkReachedUi();

        var next = new StartupCrashGuard(root);
        Assert.AreEqual(0, next.ConsecutiveFailures);
        Assert.IsFalse(next.RecoveryModeRequired);
        Assert.IsFalse(next.MarkerCorrupt);
    });

    [TestMethod]
    public void ConstructorAndMarkReachedUiNeverThrowWhenTheMarkerPathIsBlocked() => WithRoot(root =>
    {
        // Make the marker's own path a directory instead of a file, so every
        // File.WriteAllText/Move/ReadAllText against it fails with IO
        // exceptions - StartupCrashGuard must swallow these (visibly via
        // MarkerWriteFailed/MarkerCorrupt, never by throwing) rather than let
        // them crash app startup itself.
        Directory.CreateDirectory(MarkerPath(root));

        StartupCrashGuard? guard = null;
        try { guard = new StartupCrashGuard(root); guard.MarkReachedUi(); }
        catch (Exception ex) { Assert.Fail($"StartupCrashGuard must never throw out of the constructor or MarkReachedUi(); got {ex}"); }
        Assert.IsNotNull(guard);
        Assert.IsTrue(guard!.MarkerWriteFailed, "A write failure must stay visible rather than being silently swallowed.");
    });

    [TestMethod]
    public void RepeatedCorruptReadsStayDeterministicNoInfiniteGrowthOrCrash()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(MarkerPath(root), "garbage");
            for (var i = 0; i < 5; i++)
            {
                var guard = new StartupCrashGuard(root);
                Assert.IsTrue(guard.RecoveryModeRequired);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
