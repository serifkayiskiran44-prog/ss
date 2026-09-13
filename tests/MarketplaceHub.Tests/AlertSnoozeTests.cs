using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #852 (NOTIFICATIONS: Alert snooze with expiry). A snooze is bounded and ends by itself; it silences an alert only
// while the alert is no more severe than when it was snoozed (a new severe event bypasses); it survives a
// resolve/reopen cycle until it expires; the store keeps only identity, scope, severity and times -- never a title
// or a detail -- and reads back after a restart, dropping what has expired.
[TestClass]
public sealed class AlertSnoozeTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static LocalNotification Alert(string severity, string state = "Open", int reopened = 0) => new("id-1", "fp-1", severity, "etsy", "S1", "Sync başarısız", "ayrıntı", false, T0, "sync", state, 1, null, reopened);

    [TestMethod]
    public void ASnoozeIsBoundedEndsByItselfAndYieldsToEscalation()
    {
        Assert.AreEqual(AlertSnoozeRules.Min, AlertSnoozeRules.Clamp(TimeSpan.FromSeconds(10))); Assert.AreEqual(AlertSnoozeRules.Max, AlertSnoozeRules.Clamp(TimeSpan.FromDays(90))); Assert.AreEqual(TimeSpan.FromHours(4), AlertSnoozeRules.Clamp(TimeSpan.FromHours(4)));
        Assert.AreEqual(5, AlertSnoozeRules.Options.Count); Assert.AreEqual("1 gün", AlertSnoozeRules.Option("1d")!.Label); Assert.IsNull(AlertSnoozeRules.Option("1y")); Assert.IsTrue(AlertSnoozeRules.Options.All(o => o.Duration >= AlertSnoozeRules.Min && o.Duration <= AlertSnoozeRules.Max));

        var snooze = new AlertSnooze("fp-1", "sync", "etsy|S1", "WARNING", T0.AddHours(4), T0);
        Assert.IsTrue(AlertSnoozeRules.Silences(snooze, Alert("WARNING"), T0.AddHours(1)), "Same severity, before the end: silent.");
        Assert.IsTrue(AlertSnoozeRules.Silences(snooze, Alert("INFO"), T0.AddHours(1)), "Less severe: still silent.");
        Assert.IsFalse(AlertSnoozeRules.Silences(snooze, Alert("ERROR"), T0.AddHours(1)), "A new severe event bypasses the snooze.");
        Assert.IsFalse(AlertSnoozeRules.Silences(snooze, Alert("WARNING"), T0.AddHours(4)), "At the end it is over.");
        Assert.IsFalse(AlertSnoozeRules.Silences(snooze, Alert("WARNING") with { Fingerprint = "fp-2" }, T0), "Another alert is not this snooze's business.");
        Assert.IsTrue(AlertSnoozeRules.Silences(snooze, Alert("WARNING", reopened: 1), T0.AddHours(2)), "A reopen before the end stays silent: the operator asked for a time, not a sighting.");
        Assert.IsTrue(AlertSnoozeRules.Silences(snooze, Alert("Uyarı"), T0.AddHours(1)), "The board's Turkish word ranks the same.");
        StringAssert.StartsWith(AlertSnoozeRules.Describe(snooze, T0.AddHours(1)), "3 sa daha ertelendi"); StringAssert.StartsWith(AlertSnoozeRules.Describe(snooze, T0.AddMinutes(230)), "10 dk daha ertelendi"); Assert.AreEqual("ertelemesi doldu", AlertSnoozeRules.Describe(snooze, T0.AddHours(5)));
        StringAssert.StartsWith(AlertSnoozeRules.Describe(snooze with { UntilUtc = T0.AddDays(3) }, T0), "3 gün daha ertelendi");
    }

    [TestMethod]
    public void TheCentreKeepsSnoozedAlertsApartUntilExpiryOrEscalationAndResolveReopenFollowTheTime()
    {
        LocalNotification Row(string id, string fp, string severity, string state = "Open", bool ack = false, int reopened = 0) => new(id, fp, severity, "etsy", "S1", "Uyarı " + id, "d", ack, T0, "sync", state, 1, state == "Open" ? null : T0, reopened);
        var alerts = new[] { Row("a", "fp-a", "WARNING"), Row("b", "fp-b", "ERROR"), Row("c", "fp-c", "INFO", ack: true), Row("d", "fp-d", "WARNING", state: "Resolved") };
        var snoozes = new[] { new AlertSnooze("fp-a", "sync", "etsy|S1", "WARNING", T0.AddHours(1), T0), new AlertSnooze("fp-b", "sync", "etsy|S1", "WARNING", T0.AddHours(1), T0), new AlertSnooze("fp-c", "sync", "etsy|S1", "INFO", T0.AddHours(1), T0), new AlertSnooze("fp-d", "sync", "etsy|S1", "WARNING", T0.AddHours(1), T0) };
        var view = NotificationCenter.Build(alerts, snoozes, T0.AddMinutes(10));
        Assert.AreEqual(1, view.Unresolved.Sum(g => g.Count)); Assert.AreEqual("b", view.Unresolved[0].Alerts[0].Id, "The error escalated past its warning-level snooze and shows.");
        Assert.AreEqual(1, view.Snoozed.Count); Assert.AreEqual("a", view.Snoozed[0].Alert.Id);
        Assert.AreEqual(1, view.Acknowledged.Sum(g => g.Count), "An acknowledged alert is not a snooze candidate; it stays in its own section."); Assert.AreEqual(1, view.ResolvedCount, "A resolved alert is resolved, snooze or not.");
        Assert.AreEqual("1 açık uyarı: 1 hata · 1 onaylandı · 1 ertelendi · 1 çözüldü", view.Headline);
        var later = NotificationCenter.Build(alerts, snoozes, T0.AddHours(2));
        Assert.AreEqual(0, later.Snoozed.Count); Assert.AreEqual(2, later.Unresolved.Sum(g => g.Count), "After the end the snoozed alert is back.");
        var reopened = NotificationCenter.Build(new[] { Row("a", "fp-a", "WARNING", reopened: 1) }, snoozes, T0.AddMinutes(30));
        Assert.AreEqual(1, reopened.Snoozed.Count, "A reopen before the end stays snoozed.");
        Assert.AreEqual(0, NotificationCenter.Build(alerts, T0).Snoozed.Count, "Without snoozes nothing is snoozed.");
    }

    [TestMethod]
    public void TheStoreKeepsOnlyIdentityScopeAndTimesAndSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "snooze-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AlertSnoozeStore(root);
            var saved = store.Snooze("fp-1", "sync", "etsy|S1", "Uyarı", TimeSpan.FromDays(40), T0);
            Assert.AreEqual(T0 + AlertSnoozeRules.Max, saved.UntilUtc, "Clamped to the bound."); Assert.AreEqual("WARNING", saved.SeverityAtSnooze);
            store.Snooze("fp-2", "products", "", "INFO", TimeSpan.FromMinutes(30), T0);
            var active = new AlertSnoozeStore(root).Active(T0.AddMinutes(10));
            Assert.AreEqual(2, active.Count); Assert.AreEqual("fp-2", active[0].Fingerprint, "Ordered by the end time.");
            var again = new AlertSnoozeStore(root).Active(T0.AddHours(1));
            Assert.AreEqual(1, again.Count); Assert.AreEqual("fp-1", again[0].Fingerprint, "An expired snooze is dropped on read.");
            Assert.AreEqual(0, new AlertSnoozeStore(root).Active(T0.AddDays(15)).Count);
            store.Snooze("fp-3", "xml", "", "ERROR", TimeSpan.FromHours(1), T0.AddDays(15)); store.Snooze("fp-3", "xml", "", "ERROR", TimeSpan.FromHours(2), T0.AddDays(15));
            Assert.AreEqual(T0.AddDays(15).AddHours(2), store.Active(T0.AddDays(15)).Single().UntilUtc, "A second snooze replaces the first.");
            store.Clear("fp-3"); Assert.AreEqual(0, store.Active(T0.AddDays(15)).Count);
            Assert.ThrowsException<ArgumentException>(() => store.Snooze(" ", "sync", "", "ERROR", TimeSpan.FromHours(1), T0));

            // Only identity, scope, severity and times are on disk: the table has no column for words.
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "notifications.db") }.ToString()); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT name FROM pragma_table_info('AlertSnoozes')"; using var r = cmd.ExecuteReader(); var columns = new System.Collections.Generic.List<string>(); while (r.Read()) columns.Add(r.GetString(0));
            CollectionAssert.AreEquivalent(new[] { "Fingerprint", "Source", "StoreKey", "Severity", "UntilUtc", "CreatedUtc" }, columns);
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
    }
}
