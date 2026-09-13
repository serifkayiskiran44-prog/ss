using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #884 (DIAGNOSTICS UX: Safe copy summary). A failure or a recorded run becomes a support text in one piece: version,
// moment, screen, correlation, the failure's class and one-line summary, the exception chain as type names and
// sanitized messages (five levels, then a note) — never a stack frame, never a raw body — and the recent audit
// events as safe summaries; every line passes the central redaction; the clipboard reports when it is unavailable
// instead of throwing.
[TestClass]
public sealed class SupportSummaryTests
{
    [TestMethod]
    public void TheSummaryIsRedactedStackFreeAndBoundedAndTheClipboardNeverThrows()
    {
        // A nested error: the chain as types and sanitized messages, the context lines, no e-mail, no secret, no user name, no stack frame.
        var nested = new InvalidOperationException("Dış hata ali@example.com", new IOException("Orta hata C:\\Users\\serif\\feed.xml", new FormatException("İç hata token=abc123")));
        var text = SupportSummary.Compose(nested, new SupportSummaryContext(Route: "xml", SourceLabel: "Kaynak feed", Correlation: "chain-000001"));
        StringAssert.StartsWith(text, SupportSummary.Header); StringAssert.Contains(text, AppVersion.Display); StringAssert.Contains(text, "Ekran: xml"); StringAssert.Contains(text, "Kaynak: Kaynak feed"); StringAssert.Contains(text, "Korelasyon: chain-000001");
        StringAssert.Contains(text, "Sınıf: İşlem yapılamadı (kalıcı)"); StringAssert.Contains(text, "Hata: InvalidOperationException"); StringAssert.Contains(text, "İç hata 1: IOException"); StringAssert.Contains(text, "İç hata 2: FormatException");
        Assert.IsFalse(text.Contains("example.com", StringComparison.Ordinal)); Assert.IsFalse(text.Contains("abc123", StringComparison.Ordinal)); Assert.IsFalse(text.Contains("\\serif\\", StringComparison.OrdinalIgnoreCase)); StringAssert.Contains(text, "[user]");
        Assert.IsFalse(text.Contains("   at ", StringComparison.Ordinal), "no stack frame");

        // Deeper than five levels: cut with a note.
        Exception deep = new Exception("seviye 7"); for (var i = 6; i >= 1; i--) deep = new Exception($"seviye {i}", deep);
        var deepText = SupportSummary.Compose(deep);
        StringAssert.Contains(deepText, "İç hata 4"); Assert.IsFalse(deepText.Contains("İç hata 5", StringComparison.Ordinal)); StringAssert.Contains(deepText, SupportSummary.DeeperNote);

        // A connector failure: retryable, the query secret gone.
        var connector = SupportSummary.Compose(new HttpRequestException("GET https://api.example.com/v1/orders?access_token=sekret9 failed (503)"));
        StringAssert.Contains(connector, "Geçici hata"); StringAssert.Contains(connector, "yeniden denenebilir"); StringAssert.Contains(connector, "HttpRequestException"); Assert.IsFalse(connector.Contains("sekret9", StringComparison.Ordinal));

        // A database failure: the busy diagnosis and the type.
        var locked = new SqliteException("database is locked", 5);
        var db = SupportSummary.Compose(locked);
        StringAssert.Contains(db, SqliteBusyDiagnostics.Describe(locked)); StringAssert.Contains(db, "SqliteException");

        // A raw body in a message is hidden; an aggregate is unwrapped.
        var raw = SupportSummary.Compose(new InvalidOperationException("{\"token\":\"x\",\"a\":1}"));
        StringAssert.Contains(raw, StatusTooltip.RawPayloadHidden); Assert.IsFalse(raw.Contains("\"a\":1", StringComparison.Ordinal));
        StringAssert.Contains(SupportSummary.Compose(new AggregateException(new TimeoutException("zaman aşımı"))), "Hata: TimeoutException");

        // A recorded failure and its chain: the row, its store, order and correlation, the related events, the phone masked.
        var record = new AuditEvent { Module = "order", Action = "ship", Outcome = "Failed", Marketplace = "etsy", ShopId = "S1", OrderId = "1001", Detail = "Kargo başarısız; müşteri 0532 123 45 67", Correlation = "chain-000002", AtUtc = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc) };
        var related = new[] { new AuditEvent { Module = "import", Action = "run", Outcome = "OK", Detail = "okundu", Correlation = "chain-000002" }, record };
        var audit = SupportSummary.ComposeFromAudit(record, related);
        StringAssert.Contains(audit, "Kayıt: order/ship · Failed"); StringAssert.Contains(audit, "Mağaza: etsy|S1"); StringAssert.Contains(audit, "Sipariş: 1001"); StringAssert.Contains(audit, "Korelasyon: chain-000002");
        StringAssert.Contains(audit, "Son olaylar:"); StringAssert.Contains(audit, "import/run OK"); Assert.IsFalse(audit.Contains("0532", StringComparison.Ordinal)); StringAssert.Contains(audit, "[pii-phone]");

        // Bounds: ten recent events at most, a field capped to one line.
        var many = Enumerable.Range(1, 30).Select(i => new AuditEvent { Module = "m", Action = $"a{i}", Outcome = "OK", Detail = "d" }).ToList();
        var capped = SupportSummary.Compose(new Exception("x"), new SupportSummaryContext(RecentEvents: many));
        StringAssert.Contains(capped, "m/a10 OK"); Assert.IsFalse(capped.Contains("m/a11", StringComparison.Ordinal));
        Assert.IsTrue(SupportSummary.Safe(new string('z', 1000)).Length <= SupportSummary.MaxLineLength);

        // The clipboard: recorded when it works, reported when it is unavailable, never an exception.
        var original = SafeClipboard.Setter;
        try
        {
            string received = null; SafeClipboard.Setter = t => received = t;
            Assert.IsTrue(SafeClipboard.TryCopy("özet")); Assert.AreEqual("özet", received);
            SafeClipboard.Setter = _ => throw new COMException("OpenClipboard failed"); Assert.IsFalse(SafeClipboard.TryCopy("özet"));
            SafeClipboard.Setter = _ => throw new InvalidOperationException("no STA"); Assert.IsFalse(SafeClipboard.TryCopy("x"));
        }
        finally { SafeClipboard.Setter = original; }
    }
}
