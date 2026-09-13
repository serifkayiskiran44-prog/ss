using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #826 (DESIGN: Import rejected-rows export). A stable five-column schema; the import's own sentences parsed into
// row / code / field; none, one and a hundred thousand rows; a cancellation and a disk error that leave no partial
// file and are reported as results; values and messages redacted.
[TestClass]
public sealed class RejectedRowsExportTests
{
    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "rejected-" + Guid.NewGuid().ToString("N"), name);

    [TestMethod]
    public void TheSchemaIsStableAndTheImportsSentencesParseIntoIt()
    {
        CollectionAssert.AreEqual(new[] { "satir", "neden_kodu", "alan", "guvenli_deger", "mesaj" }, RejectedRowsExport.Schema.ToArray(), "A script fixing rows relies on these exact columns.");
        Assert.AreEqual(1, RejectedRowsExport.SchemaVersion);

        var negative = RejectedRowsExport.Parse("Satır 3: Stock negatif olamaz. (NEGATIVE)");
        Assert.AreEqual((3, "NEGATIVE", "Stock", "Stock negatif olamaz."), (negative.RowNumber, negative.ReasonCode, negative.Field, negative.Message));

        var identity = RejectedRowsExport.Parse("Satır 12: Ad ve kimlik boş olamaz.");
        Assert.AreEqual((12, RejectedRowsExport.UnknownCode, ""), (identity.RowNumber, identity.ReasonCode, identity.Field), "No code in the sentence: none is invented; no known field leads it: none is guessed.");

        var free = RejectedRowsExport.Parse("Çalışma sayfası bulunamadı.");
        Assert.AreEqual(0, free.RowNumber); Assert.AreEqual(RejectedRowsExport.UnknownCode, free.ReasonCode);

        var built = RejectedRowsExport.Build(new[] { "Satır 9: Price negatif olamaz. (NEGATIVE)", "Çalışma sayfası bulunamadı.", "Satır 2: Ad ve kimlik boş olamaz." });
        CollectionAssert.AreEqual(new[] { 2, 9, 0 }, built.Select(r => r.RowNumber).ToArray(), "Row order, unparsed sentences last.");
    }

    [TestMethod]
    public void NoneOneAndAHundredThousandRowsWrite()
    {
        var none = Temp("none.csv");
        var noneResult = RejectedRowsExport.WriteCsv(none, Array.Empty<RejectedRow>());
        Assert.IsTrue(noneResult.Success); Assert.AreEqual(0, noneResult.Written);
        Assert.AreEqual(1, File.ReadAllLines(none).Length, "No rows still writes the header, so the schema is visible.");

        var one = Temp("one.csv");
        Assert.IsTrue(RejectedRowsExport.WriteCsv(one, new[] { RejectedRowsExport.From(7, "DUPLICATE", "Sku", "A,B \"x\"", "Aynı SKU iki kez.") }).Success);
        var lines = File.ReadAllLines(one);
        Assert.AreEqual("satir,neden_kodu,alan,guvenli_deger,mesaj", lines[0]);
        Assert.AreEqual("7,DUPLICATE,Sku,\"A,B \"\"x\"\"\",Aynı SKU iki kez.", lines[1], "RFC 4180 quoting for commas and quotes.");

        var many = Temp("many.csv");
        var rows = Enumerable.Range(1, 100_000).Select(i => RejectedRowsExport.From(i, "NEGATIVE", "Stock", "-1", "Stock negatif olamaz.")).ToList();
        var watch = Stopwatch.StartNew();
        var manyResult = RejectedRowsExport.WriteCsv(many, rows);
        watch.Stop();
        Assert.IsTrue(manyResult.Success); Assert.AreEqual(100_000, manyResult.Written);
        Assert.AreEqual(100_001, File.ReadLines(many).Count());
        Assert.IsTrue(watch.ElapsedMilliseconds < 30_000, $"100k rows took {watch.ElapsedMilliseconds} ms.");
        Directory.Delete(Path.GetDirectoryName(many)!, true); Directory.Delete(Path.GetDirectoryName(one)!, true); Directory.Delete(Path.GetDirectoryName(none)!, true);
    }

    [TestMethod]
    public void ACancellationLeavesNoFileAndIsAResultNotAnException()
    {
        var path = Temp("cancelled.csv");
        using var cts = new CancellationTokenSource();
        var rows = new List<RejectedRow>();
        for (var i = 1; i <= 5_000; i++) rows.Add(RejectedRowsExport.From(i, "NEGATIVE", "Stock", "-1", "x"));
        cts.CancelAfter(TimeSpan.Zero);

        var result = RejectedRowsExport.WriteCsv(path, rows, cts.Token);

        Assert.IsTrue(result.Cancelled); Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "iptal");
        Assert.IsFalse(File.Exists(path), "No half-written target.");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length, "No temp file left behind.");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void ADiskErrorIsReportedWithNoPartialFile()
    {
        var directory = Temp("as-directory.csv");
        Directory.CreateDirectory(directory); // the target path is a directory: the move must fail

        var result = RejectedRowsExport.WriteCsv(directory, new[] { RejectedRowsExport.From(1, "NEGATIVE", "Stock", "-1", "x") });

        Assert.IsFalse(result.Success); Assert.IsFalse(result.Cancelled);
        StringAssert.Contains(result.Error, "yazılamadı");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(directory)!, "*.tmp-*").Length, "No temp file left behind.");
        Assert.IsFalse(RejectedRowsExport.WriteCsv("", Array.Empty<RejectedRow>()).Success, "An empty path is refused, not thrown.");
        Directory.Delete(Path.GetDirectoryName(directory)!, true);
    }

    [TestMethod]
    public void ValuesAndMessagesAreRedactedAndNeverARawBody()
    {
        var row = RejectedRowsExport.From(4, "FORMAT", "Description", "Müşteri ali@example.com · Authorization: Bearer abc.def · " + new string('x', 300), "Reddedildi: token=abc123");

        Assert.IsFalse(row.SafeValue.Contains("ali@example.com") || row.SafeValue.Contains("abc.def"), row.SafeValue);
        Assert.IsTrue(row.SafeValue.Length <= RejectedRowsExport.ValueLength);
        Assert.IsFalse(row.Message.Contains("abc123"), row.Message);
        Assert.AreEqual(StatusTooltip.RawPayloadHidden, RejectedRowsExport.SafeValue("{\"a\":1,\"b\":2}"));
        Assert.AreEqual("", RejectedRowsExport.SafeValue("  "));
    }
}
