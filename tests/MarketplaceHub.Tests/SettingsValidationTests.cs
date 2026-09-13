using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #856 (DESIGN: Settings validation banner). The model turns facts about the real stores -- never a value, only
// whether a secret exists, when a connection was last verified, whether a record still passes its rules -- into
// issues tied to the taxonomy entry that owns the fix: invalid configuration, a missing secret, a stale connection,
// a save conflict a form reported. Blocking first, then warnings, then information; inside a level the taxonomy's
// order; every text redacted and capped. The edit state carries a form's reported conflict until the form reloads
// or saves. The probe reads the real stores in a directory.
[TestClass]
public sealed class SettingsValidationTests
{
    static SettingsValidation.ConnectionFacts Facts(string channel, bool enabled = true, string status = "NOT_CONFIGURED", DateTime? lastTest = null, string lastError = "", bool saved = false, bool secret = false, string? error = null)
        => new(channel, enabled, status, lastTest, lastError, saved, secret, error);

    [TestMethod]
    public void RulesFireOnFactsOrderBySeverityThenTaxonomyAndNeverCarryAValue()
    {
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var none = Array.Empty<SettingsValidation.LocaleFacts>(); var nothing = Array.Empty<SettingsIssue>();

        // A fresh install: every channel enabled, nothing configured, nothing tested -- no issue, no banner.
        var fresh = MarketplaceConnectionCatalog.All.Select(d => Facts(d.Id)).ToList();
        Assert.AreEqual(0, SettingsValidation.Evaluate(fresh, none, nothing, now).Count);
        Assert.AreEqual("Ayar sorunu yok", SettingsValidation.Headline(nothing));

        var facts = new[]
        {
            Facts("trendyol", saved: true, secret: false),                                                        // a record without its secret
            Facts("ozon", status: "CONNECTED_READ_ONLY", lastTest: now.AddDays(-1), saved: false),                  // verified once, the credentials are gone
            Facts("ebay", saved: true, secret: true, error: "Cert ID geçersiz; token=abc123XYZ gönderildi"),       // a record that fails its rules; the message carries a specimen
            Facts("amazon", status: "CONNECTED_READ_ONLY", lastTest: now.AddDays(-45), saved: true, secret: true),  // verified long ago
            Facts("hepsiburada", status: "FAILED", lastTest: now.AddHours(-2), lastError: "HTTP 401 Authorization: Bearer xyz987", saved: true, secret: true),
            Facts("joom", saved: true, secret: true),                                                             // never verified
            Facts("wish", status: "CONNECTED_READ_ONLY", lastTest: now.AddDays(-3), saved: true, secret: true),     // healthy
            Facts("fruugo", enabled: false, status: "FAILED", lastTest: now.AddDays(-400), saved: true, secret: false), // disabled by choice: not judged
            Facts("allegro", status: "LIVE_API_BLOCKED", lastTest: now.AddDays(-100), saved: true, secret: true),   // a policy state, not staleness
            Facts("zzz", status: "CONNECTED_READ_ONLY", saved: false),                                            // no settings entry: nothing to link to
        };
        var locales = new[] { new SettingsValidation.LocaleFacts("etsy", "default", "Desteklenmeyen para birimi. TRY, USD, EUR veya GBP kullanın."), new SettingsValidation.LocaleFacts("ebay", "default", null) };
        var conflict = new SettingsIssue(SettingsIssueKind.SaveConflict, "locale", "Kayıt çakışması", "etsy/default yerel ayarı başka bir yerden değiştirildi (sürüm 1 → 2); formu yeniden yükleyin.", SeverityLevel.Blocking);
        var issues = SettingsValidation.Evaluate(facts, locales, new[] { conflict, conflict }, now);

        CollectionAssert.AreEqual(new[] { "locale", "locale", "ebay-connection", "ozon-connection", "trendyol-connection", "amazon-connection", "hepsiburada-connection", "joom-connection" }, issues.Select(i => i.EntryKey).ToArray(), "Blocking first, then warnings, then information; inside a level the taxonomy's order.");
        CollectionAssert.AreEqual(new[] { SettingsIssueKind.InvalidConfig, SettingsIssueKind.SaveConflict, SettingsIssueKind.InvalidConfig, SettingsIssueKind.MissingSecret, SettingsIssueKind.MissingSecret, SettingsIssueKind.StaleConnection, SettingsIssueKind.StaleConnection, SettingsIssueKind.StaleConnection }, issues.Select(i => i.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { SeverityLevel.Blocking, SeverityLevel.Blocking, SeverityLevel.Blocking, SeverityLevel.Blocking, SeverityLevel.Blocking, SeverityLevel.Warning, SeverityLevel.Warning, SeverityLevel.Info }, issues.Select(i => i.Severity).ToArray());
        Assert.AreEqual("8 ayar sorunu: 5 engelleyici, 2 uyarı, 1 bilgi", SettingsValidation.Headline(issues));

        StringAssert.Contains(issues[0].Title, "etsy/default"); StringAssert.Contains(issues[0].Detail, "para birimi");
        Assert.AreEqual(1, issues.Count(i => i.Kind == SettingsIssueKind.SaveConflict), "The same reported conflict twice is one row.");
        Assert.IsFalse(issues[2].Detail.Contains("abc123XYZ"), "A specimen in a validation message is redacted."); StringAssert.Contains(issues[2].Detail, "Cert ID");
        StringAssert.Contains(issues[3].Detail, "doğrulanmış", "Ozon: verified before, credentials gone.");
        StringAssert.Contains(issues[4].Detail, "gizli değer yok", "Trendyol: a record without its secret.");
        StringAssert.Contains(issues[5].Detail, "45 gün", "Amazon: the age of the last verification.");
        StringAssert.Contains(issues[6].Detail, "401"); Assert.IsFalse(issues[6].Detail.Contains("xyz987"), "A bearer value in the stored error never reaches the banner.");
        StringAssert.Contains(issues[7].Title, "doğrulanmadı", "Joom: never verified is information, not an error.");

        // Every text is capped and sanitized; a fact carries presence, never a value.
        var huge = SettingsValidation.Evaluate(new[] { Facts("wish", saved: true, secret: true, error: new string('x', 1000)) }, none, nothing, now);
        Assert.AreEqual(1, huge.Count); Assert.IsTrue(huge[0].Detail.Length <= SettingsValidation.MaxDetailLength);
        Assert.AreEqual("shipping", SettingsValidation.EntryKeyFor("navlungo")); Assert.AreEqual("ozon-connection", SettingsValidation.EntryKeyFor("ozon"));
        Assert.AreEqual("1 ayar sorunu: 1 uyarı", SettingsValidation.Headline(new[] { issues[5] }));
    }

    [TestMethod]
    public void TheEditStateCarriesAReportedConflictUntilTheFormReloadsOrSaves()
    {
        var state = new SettingsEditState(); var changed = 0; var saved = 0; state.IssuesChanged += () => changed++; state.Saved += () => saved++;
        var issue = new SettingsIssue(SettingsIssueKind.SaveConflict, "locale", "Kayıt çakışması", "sürüm 1 → 2", SeverityLevel.Blocking);
        state.Report(issue); state.Report(issue with { Detail = "sürüm 1 → 3" });
        Assert.AreEqual(1, state.Reported.Count, "One row per kind and entry; the latest detail wins."); Assert.AreEqual("sürüm 1 → 3", state.Reported[0].Detail); Assert.AreEqual(2, changed);
        Assert.IsFalse(state.Resolve(SettingsIssueKind.SaveConflict, "trendyol-connection")); Assert.AreEqual(2, changed, "Resolving what was not reported is silent.");
        Assert.IsTrue(state.Resolve(SettingsIssueKind.SaveConflict, "locale")); Assert.AreEqual(0, state.Reported.Count); Assert.AreEqual(3, changed);
        state.Report(issue); state.NotifySaved("locale");
        Assert.AreEqual(0, state.Reported.Count, "A successful save of the section clears its conflict."); Assert.AreEqual(1, saved);
        state.NotifySaved("trendyol-connection"); Assert.AreEqual(2, saved);
        Assert.ThrowsException<ArgumentException>(() => state.Report(issue with { EntryKey = " " }));
    }

    [TestMethod]
    public void TheProbeReadsTheRealStoresInADirectoryAndNeverTheValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var now = DateTime.UtcNow;
            Assert.AreEqual(0, SettingsValidation.Collect(root, null, now).Count, "A fresh directory: the registry's defaults, nothing configured.");

            // Saved Trendyol credentials that were never verified: information on the Trendyol entry.
            new TrendyolSettingsStore(Path.Combine(root, "trendyol.bin")).Save(new TrendyolSettings("123", "key-1", "s3cr3t-one", "123 - SelfIntegration"));
            var issues = SettingsValidation.Collect(root, null, now);
            Assert.AreEqual(1, issues.Count); Assert.AreEqual(SettingsIssueKind.StaleConnection, issues[0].Kind); Assert.AreEqual(SeverityLevel.Info, issues[0].Severity); Assert.AreEqual("trendyol-connection", issues[0].EntryKey);

            // A verification 40 days old: a warning naming the age.
            var registry = new MarketplaceConnectionStore(root); var trendyol = registry.List().Single(c => c.Channel == "trendyol" && c.ShopId == "default");
            registry.RecordTest(trendyol.Id, true); Backdate(root, trendyol.Id, now.AddDays(-40));
            issues = SettingsValidation.Collect(root, null, now);
            Assert.AreEqual(1, issues.Count); Assert.AreEqual(SeverityLevel.Warning, issues[0].Severity); StringAssert.Contains(issues[0].Detail, "40 gün");

            // The credentials removed after a verification: blocking.
            File.Delete(Path.Combine(root, "trendyol.bin"));
            issues = SettingsValidation.Collect(root, null, now);
            Assert.AreEqual(SettingsIssueKind.MissingSecret, issues.Single().Kind); Assert.AreEqual(SeverityLevel.Blocking, issues[0].Severity);

            // A credential file this Windows user cannot read: invalid configuration, without its bytes.
            File.WriteAllBytes(Path.Combine(root, "ozon.bin"), new byte[] { 1, 2, 3, 4 });
            issues = SettingsValidation.Collect(root, null, now);
            var ozon = issues.Single(i => i.EntryKey == "ozon-connection"); Assert.AreEqual(SettingsIssueKind.InvalidConfig, ozon.Kind); StringAssert.Contains(ozon.Detail, "okunamadı");

            // A locale row an older build wrote that no longer passes its rules: invalid configuration on the locale entry.
            _ = new LocaleSettingsStore(root); InsertLocale(root, "etsy", "default", "XXX");
            issues = SettingsValidation.Collect(root, null, now);
            var locale = issues.Single(i => i.EntryKey == "locale"); Assert.AreEqual(SettingsIssueKind.InvalidConfig, locale.Kind); StringAssert.Contains(locale.Title, "etsy/default");

            // A conflict a form reported rides along; no text carries a value that was ever stored.
            var state = new SettingsEditState(); state.Report(new SettingsIssue(SettingsIssueKind.SaveConflict, "locale", "Kayıt çakışması", "x", SeverityLevel.Blocking));
            var all = SettingsValidation.Collect(root, state, now);
            Assert.AreEqual(4, all.Count); Assert.IsFalse(all.Any(i => (i.Detail + i.Title).Contains("s3cr3t") || (i.Detail + i.Title).Contains("key-1")));
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }

    internal static void Backdate(string root, string id, DateTime whenUtc)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE MarketplaceConnections SET LastTestUtc=$t WHERE Id=$id";
        command.Parameters.AddWithValue("$t", whenUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    internal static void InsertLocale(string root, string channel, string shop, string currency)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "locale-settings.db") }.ToString()); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO StoreLocaleSettings(Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc) VALUES($c,$s,$cur,'tr-TR',20,'dd.MM.yyyy',1,$u)";
        command.Parameters.AddWithValue("$c", channel); command.Parameters.AddWithValue("$s", shop); command.Parameters.AddWithValue("$cur", currency); command.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }
}
