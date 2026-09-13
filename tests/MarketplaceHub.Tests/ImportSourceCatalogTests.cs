using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #823 (DESIGN: Import source selection clarity). Support is the reader's real rule, the picker offers only what
// the build can do, the last-used source leads, disabled and unsupported sources are labelled, and a location is
// listed with its user information and secret query parameters masked.
[TestClass]
public sealed class ImportSourceCatalogTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static bool NoFile(string _) => false;
    static bool OnlyFeedXml(string path) => path == @"C:\feeds\feed.xml";

    [TestMethod]
    public void SupportMirrorsTheReadersRuleAndSaysWhyNot()
    {
        Assert.AreEqual(ImportSourceKind.XmlUrl, ImportSourceCatalog.Classify("https://supplier.example.com/feed.xml?v=2", NoFile).Kind);
        Assert.AreEqual(ImportSourceKind.XmlFile, ImportSourceCatalog.Classify(@"C:\feeds\feed.xml", OnlyFeedXml).Kind);

        var http = ImportSourceCatalog.Classify("http://supplier.example.com/feed.xml", NoFile);
        Assert.AreEqual(ImportSourceKind.Unsupported, http.Kind); StringAssert.Contains(http.Reason, "HTTPS");
        var userInfo = ImportSourceCatalog.Classify("https://ali:gizli@supplier.example.com/feed.xml", NoFile);
        Assert.AreEqual(ImportSourceKind.Unsupported, userInfo.Kind); StringAssert.Contains(userInfo.Reason, "kullanıcı bilgisi");
        Assert.AreEqual(ImportSourceKind.Unsupported, ImportSourceCatalog.Classify("ftp://supplier.example.com/feed.xml", NoFile).Kind);
        var gone = ImportSourceCatalog.Classify(@"C:\feeds\missing.xml", OnlyFeedXml);
        Assert.AreEqual(ImportSourceKind.Unsupported, gone.Kind); StringAssert.Contains(gone.Reason, "bulunamadı");
        Assert.AreEqual(ImportSourceKind.Unsupported, ImportSourceCatalog.Classify("feed", NoFile).Kind);
        Assert.AreEqual(ImportSourceKind.Unsupported, ImportSourceCatalog.Classify("", NoFile).Kind);
    }

    [TestMethod]
    public void ALocationIsListedWithUserInfoAndSecretQueryParametersMasked()
    {
        var masked = ImportSourceCatalog.MaskLocation("https://ali:gizli123@supplier.example.com:8443/feeds/feed.xml?token=ABC123&key=K9&v=2&sig=S1");

        StringAssert.Contains(masked, "supplier.example.com:8443/feeds/feed.xml", "Host and path stay recognisable.");
        StringAssert.Contains(masked, "v=2", "A harmless parameter survives.");
        foreach (var secret in new[] { "gizli123", "ABC123", "K9", "S1", "ali:" })
            Assert.IsFalse(masked.Contains(secret, StringComparison.Ordinal), $"'{secret}' reached the list: {masked}");
        Assert.AreEqual("", ImportSourceCatalog.MaskLocation("  "));
        Assert.IsFalse(ImportSourceCatalog.MaskLocation(@"C:\Users\serif\feed.xml").Contains("serif"), "A profile path is redacted like everywhere else.");
    }

    [TestMethod]
    public void ThePickerOffersOnlyWhatThisBuildCanImport()
    {
        var withExcel = ImportSourceCatalog.SupportedTypes(route => route == "excel");
        var withoutExcel = ImportSourceCatalog.SupportedTypes(_ => false);

        CollectionAssert.AreEqual(new[] { ImportSourceKind.XmlUrl, ImportSourceKind.XmlFile, ImportSourceKind.Excel }, withExcel.Select(t => t.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { ImportSourceKind.XmlUrl, ImportSourceKind.XmlFile }, withoutExcel.Select(t => t.Kind).ToArray(), "No Excel screen, no Excel entry -- a type is never invented.");
        Assert.IsFalse(withExcel.Any(t => t.Kind == ImportSourceKind.Unsupported));
        Assert.IsTrue(withExcel.All(t => t.Label.Length > 0 && t.Hint.Length > 0));
        Assert.IsFalse(withExcel.Any(t => t.Hint.Contains("token", StringComparison.OrdinalIgnoreCase) && t.Hint.Contains('=')), "A hint never shows a credential example.");
    }

    [TestMethod]
    public void RowsLeadWithTheLastUsedThenUsableThenDisabledThenUnsupported()
    {
        var sources = new[]
        {
            new XmlSource { Id = "u", Name = "Unsupported", Location = "http://x.example.com/a.xml" },
            new XmlSource { Id = "d", Name = "Disabled", Location = "https://d.example.com/a.xml", Enabled = false },
            new XmlSource { Id = "b", Name = "Beta", Location = "https://b.example.com/a.xml" },
            new XmlSource { Id = "a", Name = "Alpha", Location = "https://a.example.com/a.xml" },
        };

        var rows = ImportSourceCatalog.Options(sources, recentId: "b", _ => null, Now, NoFile);

        CollectionAssert.AreEqual(new[] { "b", "a", "d", "u" }, rows.Select(r => r.Id).ToArray());
        Assert.IsTrue(rows[0].Recent); StringAssert.StartsWith(rows[0].Summary, "★ ");
        Assert.IsFalse(rows[2].Enabled); StringAssert.Contains(rows[2].Summary, "pasif");
        Assert.IsFalse(rows[3].Supported); StringAssert.Contains(rows[3].Summary, "desteklenmiyor"); StringAssert.Contains(rows[3].Reason, "HTTPS");
        Assert.IsTrue(rows.All(r => r.HealthLabel == "hiç denenmedi"), "No health record: the row says so instead of pretending.");
    }

    [TestMethod]
    public void HealthReadsAsStatusPlusLastSuccessAge()
    {
        var healthy = ImportSourceCatalog.HealthLabel(new SourceHealthSummary("READY", Now.AddMinutes(-5), 120, ""), Now);
        StringAssert.Contains(healthy, "ready"); StringAssert.Contains(healthy, "5 dk önce");
        var neverOk = ImportSourceCatalog.HealthLabel(new SourceHealthSummary("FAILED", null, null, "timeout"), Now);
        StringAssert.Contains(neverOk, "failed"); StringAssert.Contains(neverOk, "hiç başarılı olmadı");
        Assert.AreEqual("hiç denenmedi", ImportSourceCatalog.HealthLabel(null, Now));

        var row = ImportSourceCatalog.Describe(new XmlSource { Id = "s", Name = "  ", Location = "https://s.example.com/f.xml" }, null, false, Now, NoFile);
        Assert.AreEqual("(adsız kaynak)", row.Title, "An unnamed source still has a row.");
        Assert.AreEqual("XML adresi", row.KindLabel);
    }
}
