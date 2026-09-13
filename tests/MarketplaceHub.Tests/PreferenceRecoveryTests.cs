using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #876 (UX STATE: Corrupt preference recovery). A record whose payload cannot be understood — malformed JSON, a
// value no consumer knows, a route that no longer exists — reads as the default with a diagnostic that names the
// key and the reason and never the payload; partial fields keep what is valid; a future version keeps its #875
// fallback; a store file that is not a database is set aside and recreated so the application starts; a write that
// fails is a diagnostic, not an exception out of the caller; the diagnostics list the fallbacks and the support
// package carries them without any payload or the store itself; a reset removes the preference records, keeps the
// saved views and clears the diagnostics.
[TestClass]
public sealed class PreferenceRecoveryTests
{
    [TestMethod]
    public void CorruptPayloadsFallBackWithDiagnosticsAndTheStoreCanBeRecoveredAndReset()
    {
        var root = Path.Combine(Path.GetTempPath(), "preference-recovery-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, PreferenceSchema.StoreFileName);
        FileAttributes? attributes = null;
        try
        {
            Directory.CreateDirectory(root);
            PreferenceSchema.ClearDiagnostics();
            var store = new UiPreferenceStore(root);

            // Malformed JSON inside a valid envelope: the default, one diagnostic naming the key and the reason, never the payload; a second read adds nothing.
            const string leaked = "ali@example.com LEAKMARKER2";
            store.Set("layout:products", PreferenceSchema.Wrap(1, "{\"Version\":1,\"Columns\":[{\"Key\":\"" + leaked + "\""));
            Assert.IsFalse(PreferenceSchema.TryDecode<DataGridLayoutState>(store, "layout:products", DataGridLayoutCodec.TryDeserialize, out DataGridLayoutState _));
            Assert.IsFalse(PreferenceSchema.TryDecode<DataGridLayoutState>(store, "layout:products", DataGridLayoutCodec.TryDeserialize, out DataGridLayoutState _));
            var line = PreferenceSchema.Diagnostics.Single(d => d.StartsWith("layout:products:", StringComparison.Ordinal));
            StringAssert.Contains(line, PreferenceSchema.NotUnderstood); Assert.IsFalse(line.Contains("LEAKMARKER", StringComparison.Ordinal)); Assert.IsFalse(line.Contains("Columns", StringComparison.Ordinal));

            // A value no consumer knows; a known word in either spelling is understood.
            PreferenceSchema.Write(store, "density:products", "purple-haze LEAKMARKER3");
            Assert.IsFalse(PreferenceSchema.TryDecode<string>(store, "density:products", ProductListDensity.TryNormalize, out string _));
            Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith("density:products:", StringComparison.Ordinal) && !d.Contains("LEAKMARKER", StringComparison.Ordinal)));
            PreferenceSchema.Write(store, "density:products", "Sık");
            Assert.IsTrue(PreferenceSchema.TryDecode<string>(store, "density:products", ProductListDensity.TryNormalize, out var density)); Assert.AreEqual(ProductListDensity.Compact, density);
            Assert.IsTrue(ProductListDensity.TryNormalize("rahat", out var comfortable)); Assert.AreEqual(ProductListDensity.Comfortable, comfortable);

            // Partial fields keep what is valid: a layout column without a key is dropped and the keyed column stays; a sidebar record without a width takes the default width.
            PreferenceSchema.Write(store, "layout:products", "{\"Version\":1,\"Columns\":[{\"DisplayIndex\":0,\"Width\":90,\"Visible\":true},{\"Key\":\"Sku\",\"DisplayIndex\":1,\"Width\":120,\"Visible\":true}]}");
            Assert.IsTrue(PreferenceSchema.TryDecode<DataGridLayoutState>(store, "layout:products", DataGridLayoutCodec.TryDeserialize, out var layout));
            CollectionAssert.AreEqual(new[] { "Sku" }, layout.Columns.Select(c => c.Key).ToList());
            Assert.IsTrue(NavigationSidebar.TryParse("{\"Collapsed\":true}", out var sidebar)); Assert.IsTrue(sidebar.Collapsed); Assert.AreEqual(NavigationSidebar.Default.ExpandedWidth, sidebar.ExpandedWidth);
            Assert.IsFalse(NavigationSidebar.TryParse("[1,2]", out _)); Assert.IsFalse(NavigationSidebar.TryParse("{\"Collapsed\":", out _)); Assert.IsFalse(NavigationSidebar.TryParse("", out _));
            Assert.IsFalse(DataGridLayoutCodec.TryDeserialize("{\"Columns\":[]}", out _), "a layout without its version is not a layout");

            // The orders split: a number in the usable band and nothing else.
            Assert.IsTrue(OrderWorkspaceLayout.TryDeserialize("350", out var width)); Assert.AreEqual(350, width);
            Assert.IsFalse(OrderWorkspaceLayout.TryDeserialize("abc", out _)); Assert.IsFalse(OrderWorkspaceLayout.TryDeserialize("1e309", out _)); Assert.IsFalse(OrderWorkspaceLayout.TryDeserialize("5", out _));

            // A future version keeps its schema fallback and is listed without its payload.
            store.Set("last-route", PreferenceSchema.Wrap(7, "route-from-tomorrow LEAKMARKER4"));
            Assert.IsFalse(PreferenceSchema.TryDecode(store, "last-route", (string s, out string r) => { r = s; return true; }, out string _));
            Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith("last-route:", StringComparison.Ordinal) && d.Contains("gelecek sürüm 7", StringComparison.Ordinal) && !d.Contains("LEAKMARKER", StringComparison.Ordinal)));

            // Nothing stored: no diagnostic.
            var before = PreferenceSchema.Diagnostics.Count;
            Assert.IsFalse(PreferenceSchema.TryDecode<NavigationSidebarState>(store, "shell:sidebar", NavigationSidebar.TryParse, out _)); Assert.AreEqual(before, PreferenceSchema.Diagnostics.Count);

            // The diagnostics service lists the fallbacks; the support package carries them and never a payload or the store file.
            var check = new DiagnosticsService(root).Build().Checks.Single(c => c.Name == PreferenceSchema.DiagnosticName);
            Assert.AreEqual("WARN", check.Status); StringAssert.Contains(check.Detail, "layout:products"); StringAssert.Contains(check.Detail, "last-route"); Assert.IsFalse(check.Detail.Contains("LEAKMARKER", StringComparison.Ordinal));
            var export = Path.Combine(root, "support.zip"); SupportPackageService.Export(export, root);
            using (var archive = ZipFile.OpenRead(export))
            {
                Assert.IsFalse(archive.Entries.Any(e => e.Name.Contains("ui-preferences", StringComparison.OrdinalIgnoreCase)), "the preference store itself never ships");
                var content = string.Join("\n", archive.Entries.Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
                StringAssert.Contains(content, "layout:products"); StringAssert.Contains(content, "last-route");
                Assert.IsFalse(content.Contains("LEAKMARKER", StringComparison.Ordinal)); Assert.IsFalse(content.Contains("example.com", StringComparison.Ordinal)); Assert.IsFalse(content.Contains("purple-haze", StringComparison.Ordinal)); Assert.IsFalse(content.Contains("route-from-tomorrow", StringComparison.Ordinal));
            }

            // A reset removes the preference records, keeps the saved views and clears the diagnostics; the check is OK again.
            store.SaveView("orders", "Bugün", "{\"status\":\"open\"}");
            Assert.AreEqual(3, PreferenceSchema.Reset(store));
            Assert.AreEqual(0, store.Keys().Count); Assert.AreEqual(1, store.ListViews("orders").Count); Assert.AreEqual(0, PreferenceSchema.Diagnostics.Count);
            Assert.AreEqual("OK", new DiagnosticsService(root).Build().Checks.Single(c => c.Name == PreferenceSchema.DiagnosticName).Status);

            // A store file that is not a database: the plain store refuses it; the schema's opener sets it aside with its journal, recreates an empty store and names the file in the diagnostics; the next start is quiet.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.WriteAllText(file, "this is not a database LEAKMARKER9"); File.WriteAllText(file + "-wal", "stale");
            Assert.ThrowsException<Microsoft.Data.Sqlite.SqliteException>(() => new UiPreferenceStore(root));
            var recovered = PreferenceSchema.OpenStore(root);
            PreferenceSchema.Write(recovered, "density:products", "compact");
            Assert.AreEqual("compact", PreferenceSchema.Read(recovered, "density:products"));
            var quarantined = Directory.GetFiles(root, "ui-preferences.corrupt-*.db");
            Assert.AreEqual(1, quarantined.Length); Assert.AreEqual("this is not a database LEAKMARKER9", File.ReadAllText(quarantined[0]));
            // SQLite itself may remove an unusable journal when the failed connection closes; either way nothing stale stays next to the fresh store.
            Assert.IsFalse(IsStale(file + "-wal"), "no stale journal stays behind next to the fresh store");
            var note = PreferenceSchema.Diagnostics.Single(d => d.StartsWith(PreferenceSchema.StoreFileName + ":", StringComparison.Ordinal));
            StringAssert.Contains(note, Path.GetFileName(quarantined[0])); Assert.IsFalse(note.Contains("LEAKMARKER", StringComparison.Ordinal)); Assert.IsFalse(note.Contains(root, StringComparison.OrdinalIgnoreCase));
            var count = PreferenceSchema.Diagnostics.Count;
            Assert.AreEqual("compact", PreferenceSchema.Read(PreferenceSchema.OpenStore(root), "density:products"), "a restart reads the recreated store");
            Assert.AreEqual(count, PreferenceSchema.Diagnostics.Count, "a healthy store adds nothing");

            // A write that fails is a diagnostic, never an exception out of the caller; the personal-data refusal still throws.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            attributes = File.GetAttributes(file); File.SetAttributes(file, attributes.Value | FileAttributes.ReadOnly);
            PreferenceSchema.Write(recovered, "last-route", "orders");
            Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith("last-route:", StringComparison.Ordinal) && d.Contains("yazılamadı", StringComparison.Ordinal)), string.Join(" | ", PreferenceSchema.Diagnostics));
            Assert.ThrowsException<InvalidOperationException>(() => PreferenceSchema.Write(recovered, "last-route", "ali@example.com"));
            File.SetAttributes(file, attributes.Value); attributes = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            try { if (attributes is not null && File.Exists(file)) File.SetAttributes(file, attributes.Value); } catch (IOException) { }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }

    /// <summary>Whether the file still holds the hand-written "stale" bytes (read share-tolerant: the fresh store may hold its own journal open).</summary>
    static bool IsStale(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[8]; var read = stream.Read(buffer, 0, buffer.Length);
        return read == 5 && System.Text.Encoding.ASCII.GetString(buffer, 0, read) == "stale";
    }
}
