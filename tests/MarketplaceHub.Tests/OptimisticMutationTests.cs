using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #874 (DESIGN: Optimistic UI rollback pattern). The screen takes a change at once, the store is asked to keep it,
// and any failure — a validation, a database failure, a cancellation — brings the prior state back with a visible,
// redacted error; a second click while the first is in flight is refused before it touches anything; a live
// marketplace write is never optimistic.
[TestClass]
public sealed class OptimisticMutationTests
{
    [TestMethod]
    public async Task SuccessCommitsAndEveryFailureRevertsWithAVisibleErrorWhileDuplicatesAndLiveWritesAreRefused()
    {
        var applied = 0; var reverted = 0; var errors = new List<string>();
        void Apply() => applied++; void Revert() => reverted++; void Report(string e) => errors.Add(e);

        // Success: applied once, committed, nothing reverted, nothing reported.
        var ok = OptimisticMutation.Run("ack:1", Apply, () => { }, Revert, Report);
        Assert.AreEqual(MutationOutcome.Committed, ok.Outcome); Assert.AreEqual(1, applied); Assert.AreEqual(0, reverted); Assert.AreEqual(0, errors.Count); Assert.IsFalse(OptimisticMutation.IsInFlight("ack:1"));

        // Validation failure: the store refuses; the prior state comes back and the error names the rule.
        var invalid = OptimisticMutation.Run("save:2", Apply, () => throw new InvalidOperationException("Ad zorunlu."), Revert, Report);
        Assert.AreEqual(MutationOutcome.Reverted, invalid.Outcome); Assert.AreEqual(1, reverted); StringAssert.Contains(errors[^1], "Ad zorunlu."); StringAssert.Contains(errors[^1], "önceki durum geri alındı");

        // Database failure: the same, through the database's own exception.
        var db = OptimisticMutation.Run("save:3", Apply, () => throw new SqliteException("database is locked", 5), Revert, Report);
        Assert.AreEqual(MutationOutcome.Reverted, db.Outcome); Assert.AreEqual(2, reverted); StringAssert.Contains(errors[^1], "database is locked");

        // A secret in a failure never reaches the screen.
        OptimisticMutation.Run("save:4", Apply, () => throw new InvalidOperationException("Reddedildi: Authorization: Bearer abc123xyz"), Revert, Report);
        Assert.IsFalse(errors[^1].Contains("abc123xyz")); StringAssert.Contains(errors[^1], "Reddedildi");

        // Cancellation: reverted with the cancellation words, both when the token is already cancelled and when the commit observes it.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var byToken = await OptimisticMutation.RunAsync("snooze:5", Apply, _ => Task.CompletedTask, Revert, Report, cancelled.Token);
        Assert.AreEqual(MutationOutcome.Reverted, byToken.Outcome); Assert.AreEqual(OptimisticMutation.Cancelled, byToken.Error);
        var byCommit = await OptimisticMutation.RunAsync("snooze:6", Apply, _ => throw new OperationCanceledException(), Revert, Report);
        Assert.AreEqual(MutationOutcome.Reverted, byCommit.Outcome); Assert.AreEqual(OptimisticMutation.Cancelled, byCommit.Error);

        // Duplicate click: while the first commit is in flight, the second with the same key is refused before it applies anything; the first still commits.
        var gate = new TaskCompletionSource(); var appliedBefore = applied;
        var first = OptimisticMutation.RunAsync("ack:7", Apply, _ => gate.Task, Revert, Report);
        Assert.IsTrue(OptimisticMutation.IsInFlight("ack:7"));
        var second = await OptimisticMutation.RunAsync("ack:7", Apply, _ => Task.CompletedTask, Revert, Report);
        Assert.AreEqual(MutationOutcome.Refused, second.Outcome); Assert.AreEqual(OptimisticMutation.DuplicateRefused, second.Error); Assert.AreEqual(appliedBefore + 1, applied, "the second click applied nothing");
        gate.SetResult(); Assert.AreEqual(MutationOutcome.Committed, (await first).Outcome); Assert.IsFalse(OptimisticMutation.IsInFlight("ack:7"));
        // A different key is independent.
        Assert.AreEqual(MutationOutcome.Committed, OptimisticMutation.Run("ack:8", Apply, () => { }, Revert, Report).Outcome);

        // A live marketplace write is refused before anything changes: no apply, no commit, the refusal on screen.
        var committed = false; var live = OptimisticMutation.Run("push:9", Apply, () => committed = true, Revert, Report, MutationScope.LiveMarketplace);
        Assert.AreEqual(MutationOutcome.Refused, live.Outcome); Assert.IsFalse(committed); Assert.AreEqual(OptimisticMutation.LiveWriteRefused, errors[^1]); Assert.AreEqual(appliedBefore + 2, applied, "the live write applied nothing");
        var liveAsync = await OptimisticMutation.RunAsync("push:10", Apply, _ => { committed = true; return Task.CompletedTask; }, Revert, Report, default, MutationScope.LiveMarketplace);
        Assert.AreEqual(MutationOutcome.Refused, liveAsync.Outcome); Assert.IsFalse(committed);

        // A failure after a refusal still leaves the key free.
        Assert.IsFalse(OptimisticMutation.IsInFlight("push:9"));

        // A revert that fails itself (the store refuses even the read that redraws) is reported in the same words, never thrown out of a click.
        var doubleFailure = OptimisticMutation.Run("ack:11", Apply, () => throw new SqliteException("attempt to write a readonly database", 8), () => throw new SqliteException("attempt to write a readonly database", 8), Report);
        Assert.AreEqual(MutationOutcome.Reverted, doubleFailure.Outcome); StringAssert.Contains(doubleFailure.Error, OptimisticMutation.RevertedPrefix); StringAssert.Contains(doubleFailure.Error, OptimisticMutation.RevertFailedPrefix); Assert.IsFalse(OptimisticMutation.IsInFlight("ack:11"));
    }
}
