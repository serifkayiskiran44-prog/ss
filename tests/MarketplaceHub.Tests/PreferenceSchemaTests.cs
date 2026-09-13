using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #875 (UX STATE: View preference schema versioning). Every persisted UI preference is written inside an envelope
// that names its schema version; a bare record from before the envelope is version 0, brought forward by its
// family and re-saved; a future version or a corrupt envelope falls back to the defaults with a diagnostic that
// names the key and the reason, never the payload; a payload carrying personal data or a secret is refused; a
// layout that names a removed column drops it; a value survives a restart of the store.
[TestClass]
public sealed class PreferenceSchemaTests
{
    [TestMethod]
    public void RecordsAreVersionedMigratedFallenBackAndGuardedAcrossARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "preference-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            PreferenceSchema.ClearDiagnostics();
            var store = new UiPreferenceStore(root);

            // The envelope: a version and a payload; a bare legacy JSON object is not an envelope; a broken one is not either.
            var wrapped = PreferenceSchema.Wrap(1, "{\"columns\":[]}");
            Assert.IsTrue(PreferenceSchema.TryUnwrap(wrapped, out var version, out var payload)); Assert.AreEqual(1, version); Assert.AreEqual("{\"columns\":[]}", payload);
            Assert.IsFalse(PreferenceSchema.TryUnwrap("{\"columns\":[]}", out _, out _), "a bare legacy layout is not an envelope");
            Assert.IsFalse(PreferenceSchema.TryUnwrap("compact", out _, out _)); Assert.IsFalse(PreferenceSchema.TryUnwrap("{\"schema\":1,\"payload\":", out _, out _));

            // Missing.
            Assert.AreEqual(PreferenceReadOutcome.Missing, PreferenceSchema.Inspect(store, "density:products").Outcome); Assert.IsNull(PreferenceSchema.Read(store, "density:products"));

            // Old schema: a bare value written before the envelope is brought forward and re-saved; the next read is current.
            store.Set("density:products", "compact");
            var legacy = PreferenceSchema.Inspect(store, "density:products");
            Assert.AreEqual(PreferenceReadOutcome.Migrated, legacy.Outcome); Assert.AreEqual("compact", legacy.Payload); Assert.AreEqual(1, legacy.Version);
            Assert.IsTrue(PreferenceSchema.TryUnwrap(store.Get("density:products")!, out var saved, out _)); Assert.AreEqual(1, saved, "re-saved in the current envelope");
            Assert.AreEqual(PreferenceReadOutcome.Current, PreferenceSchema.Inspect(store, "density:products").Outcome);

            // New schema written through the guard: current on read, and the same after a restart of the store.
            PreferenceSchema.Write(store, "layout:products", "{\"columns\":[{\"key\":\"Sku\",\"width\":135}]}");
            Assert.AreEqual(PreferenceReadOutcome.Current, PreferenceSchema.Inspect(store, "layout:products").Outcome);
            var restarted = new UiPreferenceStore(root);
            var afterRestart = PreferenceSchema.Inspect(restarted, "layout:products"); Assert.AreEqual(PreferenceReadOutcome.Current, afterRestart.Outcome); StringAssert.Contains(afterRestart.Payload, "Sku");

            // A future version: the defaults, with a diagnostic that names the key and the reason and never the payload.
            store.Set("last-route", PreferenceSchema.Wrap(9, "orders"));
            var future = PreferenceSchema.Inspect(store, "last-route");
            Assert.AreEqual(PreferenceReadOutcome.Fallback, future.Outcome); Assert.IsNull(future.Payload); StringAssert.Contains(future.Diagnostic, "gelecek sürüm 9");
            Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith("last-route:") && d.Contains("gelecek sürüm") && !d.Contains("orders")));

            // A corrupt envelope: the defaults, with a diagnostic.
            store.Set("shell:sidebar", "{\"schema\":1,\"payload\":");
            var corrupt = PreferenceSchema.Inspect(store, "shell:sidebar");
            Assert.AreEqual(PreferenceReadOutcome.Fallback, corrupt.Outcome); StringAssert.Contains(corrupt.Diagnostic, "bozuk");

            // A family whose migration cannot bring an old record forward: the defaults, with a diagnostic.
            PreferenceSchema.Register(new PreferenceFamily("test-family", "test:", 2, (from, _) => from == 1 ? "v2" : null));
            store.Set("test:a", "old-bare-value"); Assert.AreEqual(PreferenceReadOutcome.Fallback, PreferenceSchema.Inspect(store, "test:a").Outcome);
            store.Set("test:b", PreferenceSchema.Wrap(1, "v1")); var stepped = PreferenceSchema.Inspect(store, "test:b"); Assert.AreEqual(PreferenceReadOutcome.Migrated, stepped.Outcome); Assert.AreEqual("v2", stepped.Payload); Assert.AreEqual(2, stepped.Version);

            // A payload that carries personal data or a secret is refused before it is written; ordinary payloads pass, a GUID id included.
            Assert.ThrowsException<InvalidOperationException>(() => PreferenceSchema.Write(store, "columns:products", "ali@example.com"));
            Assert.ThrowsException<InvalidOperationException>(() => PreferenceSchema.Write(store, "columns:products", "Authorization: Bearer abc123xyz"));
            Assert.IsNull(store.Get("columns:products"), "nothing was written");
            PreferenceSchema.Write(store, "xml:last-source", Guid.NewGuid().ToString("N")); Assert.AreEqual(PreferenceReadOutcome.Current, PreferenceSchema.Inspect(store, "xml:last-source").Outcome);
            PreferenceSchema.Write(store, "columns:products", "BarkodGTIN"); Assert.AreEqual("BarkodGTIN", PreferenceSchema.Read(store, "columns:products"));

            // The registry covers every preference key the app writes.
            foreach (var prefix in new[] { "layout:products", "density:products", "columns:products", "last-route", "shell:sidebar", "xml:last-source", "filter:dashboard-store", "layout:orders:preset", "layout:orders:custom", "layout:orders:split", "report-columns:" })
                Assert.IsTrue(PreferenceSchema.Families.Any(f => f.KeyPrefix == prefix), prefix);
            Assert.AreEqual("report-columns", PreferenceSchema.FamilyFor(ReportColumns.PreferenceKey("orders-csv")).Name);
            Assert.AreEqual("unregistered", PreferenceSchema.FamilyFor("brand-new:key").Name, "a new key is not refused, it is version 1");

            // A layout that names a removed column drops it and keeps the rest in order.
            var persisted = new[] { new DataGridColumnLayout("Sku", 0, 100, true), new DataGridColumnLayout("Removed", 1, 80, true), new DataGridColumnLayout("Name", 2, 200, true) };
            CollectionAssert.AreEqual(new[] { "Sku", "Name" }, DataGridLayoutCodec.ResolveOrder(new[] { "Name", "Sku" }, persisted).ToList());
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
