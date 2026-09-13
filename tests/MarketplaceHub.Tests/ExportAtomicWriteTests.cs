using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #881 (EXPORT: Atomic file write completion). Every export is produced into a temporary file next to its target and
// only a complete, uncancelled temporary is moved onto the target: a producer that throws (a full disk), a
// cancellation before or after the producer wrote, or a producer that wrote nothing leaves neither a partial final
// file nor a temporary; a stale temporary of the same target left by a crash before the rename is swept by the next
// write while a younger one and another target's are left alone; an existing target without consent is refused with
// the temporary removed; the temporary lives in the target's own directory (its permissions) with its extension.
[TestClass]
public sealed class ExportAtomicWriteTests
{
    [TestMethod]
    public void AFailedCancelledOrEmptyProducerLeavesNoFinalFileAndNoTemporaryAndStaleTemporariesAreSwept()
    {
        var root = Path.Combine(Path.GetTempPath(), "export-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "out"); var target = Path.Combine(directory, "report.csv");
            string seen = null;

            // A complete write lands; the temporary lived next to the target with the target's extension and is gone afterwards.
            ExportFiles.Write(target, overwrite: false, CancellationToken.None, temp => { seen = temp; File.WriteAllText(temp, "ok"); });
            Assert.AreEqual(directory, Path.GetDirectoryName(seen)); StringAssert.EndsWith(seen, ".csv"); StringAssert.Contains(seen, ExportFiles.TemporaryMarker); StringAssert.StartsWith(Path.GetFileName(seen), "report.tmp-");
            Assert.IsFalse(File.Exists(seen)); Assert.AreEqual("ok", File.ReadAllText(target));

            // Disk full mid-way: the producer wrote half and threw — the target is untouched and no temporary remains.
            var full = Assert.ThrowsException<IOException>(() => ExportFiles.Write(target, overwrite: true, CancellationToken.None, temp => { File.WriteAllText(temp, "half"); throw new IOException("There is not enough space on the disk."); }));
            StringAssert.Contains(full.Message, "space"); Assert.AreEqual("ok", File.ReadAllText(target)); Assert.IsFalse(Temporaries(directory).Any());

            // Cancellation before the producer: nothing is written; after the producer finished: the complete temporary is discarded — the operator asked for no file.
            using (var early = new CancellationTokenSource()) { early.Cancel(); Assert.ThrowsException<OperationCanceledException>(() => ExportFiles.Write(target, overwrite: true, early.Token, temp => File.WriteAllText(temp, "early"))); }
            using (var late = new CancellationTokenSource()) { Assert.ThrowsException<OperationCanceledException>(() => ExportFiles.Write(target, overwrite: true, late.Token, temp => { File.WriteAllText(temp, "late"); late.Cancel(); })); }
            Assert.AreEqual("ok", File.ReadAllText(target)); Assert.IsFalse(Temporaries(directory).Any());

            // A producer that wrote nothing: no final file appears, not even an empty one.
            var empty = Path.Combine(directory, "empty.csv");
            Assert.ThrowsException<IOException>(() => ExportFiles.Write(empty, overwrite: false, CancellationToken.None, _ => { }));
            Assert.IsFalse(File.Exists(empty)); Assert.IsFalse(Temporaries(directory).Any());

            // Crash before the rename: a stale temporary of the same target is swept by the next write; a younger one and another target's stay.
            var stale = Path.Combine(directory, "report.tmp-deadbeef0001.csv"); File.WriteAllText(stale, "orphan"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
            var recent = Path.Combine(directory, "report.tmp-deadbeef0002.csv"); File.WriteAllText(recent, "in progress");
            var other = Path.Combine(directory, "other.tmp-deadbeef0003.csv"); File.WriteAllText(other, "other"); File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));
            ExportFiles.Write(target, overwrite: true, CancellationToken.None, temp => File.WriteAllText(temp, "again"));
            Assert.IsFalse(File.Exists(stale), "the orphan of an earlier crash is gone"); Assert.IsTrue(File.Exists(recent), "a temporary inside the stale window may belong to a run in progress"); Assert.IsTrue(File.Exists(other), "another target's temporary is not this write's business");
            Assert.AreEqual("again", File.ReadAllText(target));
            Assert.AreEqual(1, ExportFiles.SweepStale(Path.Combine(directory, "other.csv"), TimeSpan.Zero), "a sweep is addressed by the target, not by a temporary"); Assert.IsFalse(File.Exists(other));
            File.Delete(recent);

            // An existing target without consent: refused, the temporary removed, the target intact.
            Assert.ThrowsException<ExportFileExistsException>(() => ExportFiles.Write(target, overwrite: false, CancellationToken.None, temp => File.WriteAllText(temp, "no")));
            Assert.AreEqual("again", File.ReadAllText(target)); Assert.IsFalse(Temporaries(directory).Any());

            // The asynchronous form behaves the same for a producer that throws after awaiting.
            var asyncFailure = Assert.ThrowsExceptionAsync<IOException>(() => ExportFiles.WriteAsync(target, overwrite: true, CancellationToken.None, async temp => { await Task.Delay(10); File.WriteAllText(temp, "half"); throw new IOException("disk"); })).GetAwaiter().GetResult();
            Assert.AreEqual("disk", asyncFailure.Message); Assert.AreEqual("again", File.ReadAllText(target)); Assert.IsFalse(Temporaries(directory).Any());
            ExportFiles.WriteAsync(target, overwrite: true, CancellationToken.None, async temp => { await Task.Delay(10); File.WriteAllText(temp, "async"); }).GetAwaiter().GetResult();
            Assert.AreEqual("async", File.ReadAllText(target));

            // The real rejected-rows writer cancelled mid-way: no final file, no temporary, a cancelled result.
            var csv = Path.Combine(directory, "rejected.csv"); var rows = Enumerable.Range(1, 50).Select(i => RejectedRowsExport.From(i, "NEGATIVE", "Stock", "-1", "x")).ToList();
            using var cts = new CancellationTokenSource(); cts.Cancel();
            var cancelled = RejectedRowsExport.WriteCsv(csv, rows, cts.Token);
            Assert.IsTrue(cancelled.Cancelled); Assert.IsFalse(File.Exists(csv)); Assert.IsFalse(Temporaries(directory).Any());
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }

    static string[] Temporaries(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Where(f => f.Contains(ExportFiles.TemporaryMarker, StringComparison.Ordinal)).ToArray() : Array.Empty<string>();
}
