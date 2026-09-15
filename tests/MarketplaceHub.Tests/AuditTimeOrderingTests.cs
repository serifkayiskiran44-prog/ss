using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2659: AuditStore must normalize AtUtc to a single canonical
/// UTC instant at write time, and List()/LastFailure()/retention must order
/// by the true parsed instant rather than SQL's lexical ORDER BY on the
/// stored TEXT column - which breaks for legacy rows written with mixed
/// offsets/DateTimeKind.
[TestClass]
public sealed class AuditTimeOrderingTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "audit-time-order-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawRow(string root, string id, string atUtcText, string outcome = "Info")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "audit.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AuditEvents VALUES($id,$at,'system','action','','','','',$outcome,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$at", atUtcText); cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void UtcLocalAndOffsetFixturesForTheSameInstantAreTreatedIdentically() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        var instantUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        InsertRawRow(root, "a", instantUtc.ToString("O")); // "...Z"
        InsertRawRow(root, "b", "2026-06-01T15:00:00.0000000+03:00"); // same instant, +03:00
        InsertRawRow(root, "c", "2026-06-01T07:00:00.0000000-05:00"); // same instant, -05:00

        var events = store.List();
        Assert.AreEqual(3, events.Count);
        Assert.IsTrue(events.All(e => e.AtUtc == instantUtc), "All three representations of the same instant must decode to the identical UTC DateTime.");
    });

    [TestMethod]
    public void LastFailureReturnsTheTrueNewestEvenWhenLexicalOrderIsReversed() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        // "...+05:00" sorts lexically AFTER "...Z" even though the +05:00 offset
        // pushes the actual UTC instant earlier - this is exactly the trap pure
        // text ORDER BY falls into.
        InsertRawRow(root, "older-lexically-later", "2026-06-01T10:00:00.0000000+05:00", "Failed"); // true UTC: 05:00
        InsertRawRow(root, "newer-lexically-earlier", "2026-06-01T06:00:00.0000000Z", "Failed"); // true UTC: 06:00

        var last = store.LastFailure();
        Assert.IsNotNull(last);
        Assert.AreEqual("newer-lexically-earlier", last!.Id);
    });

    [TestMethod]
    public void RetentionKeepsTheChronologicallyNewestRowsEvenWithMixedLegacyFormats() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        // Insert RetentionLimit-1 rows with normal UTC "Z" timestamps spread over
        // the past, then one legacy-style row whose lexical text would sort as
        // "very old" but whose true instant is actually the newest of all.
        var baseTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < AuditStore.RetentionLimit - 1; i++) InsertRawRow(root, "seed-" + i, baseTime.AddMinutes(i).ToString("O"));
        // Lexically this starts with "0" (before "2"), but +14:00 pushes the
        // real UTC instant to far in the future - it must survive retention.
        var trulyNewestLocalText = new DateTime(2030, 1, 1, 14, 0, 0).ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "+14:00";
        InsertRawRow(root, "legacy-newest", trulyNewestLocalText);

        // Trigger the trim by appending one more normal event.
        store.Append(new AuditEvent { Module = "m", Action = "trigger" });

        var ids = store.List(AuditStore.RetentionLimit).Select(e => e.Id).ToHashSet();
        Assert.IsTrue(ids.Contains("legacy-newest"), "The chronologically newest row must survive retention even though its legacy text sorts lexically as oldest.");
    });

    [TestMethod]
    public void AppendNormalizesLocalKindToItsTrueUtcInstant() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        var utcNow = DateTime.UtcNow;
        var localNow = utcNow.ToLocalTime(); // DateTimeKind.Local
        store.Append(new AuditEvent { Module = "m", Action = "a", AtUtc = localNow });

        var saved = store.List().Single();
        Assert.IsTrue(Math.Abs((saved.AtUtc - utcNow).TotalSeconds) < 2, "A Local-kind AtUtc must be converted to its true UTC instant, not stored as-is.");
    });

    [TestMethod]
    public void AppendTreatsUnspecifiedKindAsAlreadyUtcDeterministically() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        var unspecified = new DateTime(2026, 3, 15, 8, 30, 0, DateTimeKind.Unspecified);
        store.Append(new AuditEvent { Module = "m", Action = "a", AtUtc = unspecified });

        var saved = store.List().Single();
        Assert.AreEqual(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), saved.AtUtc, "Unspecified must be treated as already-UTC, not silently reinterpreted via the machine's local zone.");
    });

    [TestMethod]
    public void UtcKindRoundTripsUnchanged() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        var at = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
        store.Append(new AuditEvent { Module = "m", Action = "a", AtUtc = at });
        Assert.AreEqual(at, store.List().Single().AtUtc);
    });

    [TestMethod]
    public void MalformedAtUtcCorruptionIsolationStillWorksAlongsideOrdering() => WithRoot(root =>
    {
        var store = new AuditStore(root);
        store.Append(new AuditEvent { Module = "m", Action = "healthy" });
        InsertRawRow(root, "bad", "not-a-date");

        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptEvents().Count);
    });
}
