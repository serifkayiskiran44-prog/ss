using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #847 (DESIGN: Report parameter panel). Schemas follow the report; defaults are the first offered store, the last 30
// days and every state; validation names the field: no store, a store not offered, an inverted or over-long
// range, an unknown state, an over-long query, a future end date only warns. Saved filters round-trip their
// fields and nothing else; garbage loads as null and hostile values as empty. The orders runner queries only the
// store, range and state asked for, exports the chosen columns, records every outcome, and refuses wrong-store,
// invalid or foreign work.
[TestClass]
public sealed class ReportParametersTests
{
    static readonly DateTime Now = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
    static ReportDefinition Def(string key) => ReportCatalog.Find(key)!;

    [TestMethod]
    public void SchemasDefaultsAndValidationFollowTheReport()
    {
        var csv = Def("orders-csv"); var schema = ReportParameters.SchemaFor(csv);
        Assert.IsTrue(schema.Store && schema.DateRange && schema.DeliveryState && schema.Query);
        Assert.IsFalse(ReportParameters.SchemaFor(Def("support-package")).Any); Assert.IsFalse(ReportParameters.SchemaFor(Def("api-health")).Query);
        var quality = ReportParameters.SchemaFor(Def("data-quality")); Assert.IsTrue(quality.Query && !quality.Store && !quality.DateRange && !quality.DeliveryState);

        var defaults = ReportParameters.Defaults(csv, new[] { "etsy|S1", "ebay|E1" }, Now);
        Assert.AreEqual("etsy|S1", defaults.StoreKey); Assert.AreEqual(Now.Date.AddDays(-30), defaults.FromUtc); Assert.AreEqual(Now.Date, defaults.ToUtc); Assert.AreEqual("", defaults.DeliveryState); Assert.AreEqual("", defaults.Query);
        Assert.AreEqual("", ReportParameters.Defaults(csv, Array.Empty<string>(), Now).StoreKey, "No offered store, no default store.");
        var qualityDefaults = ReportParameters.Defaults(Def("data-quality"), new[] { "etsy|S1" }, Now);
        Assert.IsNull(qualityDefaults.FromUtc); Assert.AreEqual("", qualityDefaults.StoreKey, "A global report takes no store even when stores are offered.");
        Assert.AreEqual(0, ReportParameters.Validate(defaults, csv, new[] { "etsy|S1" }, Now).Count);

        string First(ReportParameterSet p, IReadOnlyCollection<string>? allowed = null) => ReportParameters.Validate(p, csv, allowed ?? new[] { "etsy|S1" }, Now).FirstOrDefault(f => f.Level == SeverityLevel.Blocking)?.Message ?? "";
        StringAssert.Contains(First(defaults with { StoreKey = "" }), "Mağaza seçin");
        StringAssert.Contains(First(defaults with { StoreKey = "ebay|E1" }), "sunulmuyor");
        StringAssert.Contains(First(defaults with { FromUtc = Now.Date, ToUtc = Now.Date.AddDays(-1) }), "bitişten sonra olamaz");
        StringAssert.Contains(First(defaults with { FromUtc = Now.Date.AddDays(-400) }), "en fazla 366 gün");
        StringAssert.Contains(First(defaults with { ToUtc = null }), "Başlangıç ve bitiş");
        StringAssert.Contains(First(defaults with { DeliveryState = "Teleported" }), "Bilinmeyen teslimat");
        StringAssert.Contains(First(defaults with { Query = new string('a', 121) }), "en fazla 120");
        StringAssert.Contains(First(defaults with { Query = "a\tb" }), "kontrol karakteri");
        var future = ReportParameters.Validate(defaults with { ToUtc = Now.Date.AddDays(10) }, csv, new[] { "etsy|S1" }, Now);
        Assert.IsTrue(ReportParameters.IsValid(future)); Assert.AreEqual(SeverityLevel.Warning, future.Single().Level); Assert.AreEqual("to", future.Single().Field);
        var stray = ReportParameters.Validate(new("data-quality", StoreKey: "etsy|S1"), Def("data-quality"), null, Now);
        Assert.IsTrue(ReportParameters.IsValid(stray)); Assert.AreEqual(SeverityLevel.Warning, stray.Single().Level);
        Assert.IsTrue(ReportParameters.IsValid(ReportParameters.Validate(defaults with { StoreKey = "ebay|E1" }, csv, null, Now)), "With no store list the shell restricts nothing.");
        Assert.AreEqual(("etsy", "S1"), ReportParameters.SplitStore(" etsy|S1 ")); Assert.AreEqual("etsy · S1", ReportParameters.StoreLabel("etsy|S1")); Assert.AreEqual("etsy", ReportParameters.StoreLabel("etsy"));
    }

    [TestMethod]
    public void SavedFiltersRoundTripOnlyTheirFieldsAndRefuseGarbage()
    {
        var p = new ReportParameterSet("orders-csv", "etsy|S1", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), "InTransit", "kupa");
        var json = ReportParameters.Serialize(p);
        StringAssert.Contains(json, "2026-08-01"); Assert.IsFalse(json.Contains("ReportKey", StringComparison.Ordinal));
        Assert.AreEqual(p, ReportParameters.Deserialize("orders-csv", json));
        Assert.IsNull(ReportParameters.Deserialize("orders-csv", "not json")); Assert.IsNull(ReportParameters.Deserialize("orders-csv", "")); Assert.IsNull(ReportParameters.Deserialize("orders-csv", "[1,2]"));
        Assert.IsNull(ReportParameters.Deserialize("orders-csv", "{\"Store\":\"" + new string('x', 5000) + "\"}"), "An oversized payload is not a filter.");
        var hostile = ReportParameters.Deserialize("orders-csv", "{\"Store\":\"  etsy|S1 \",\"From\":\"yesterday\",\"To\":\"2026-13-40\",\"State\":\"Teleported\",\"Query\":\"a\\u0007b\",\"Token\":\"secret\"}");
        Assert.IsNotNull(hostile); Assert.AreEqual("etsy|S1", hostile!.StoreKey); Assert.IsNull(hostile.FromUtc); Assert.IsNull(hostile.ToUtc); Assert.AreEqual("", hostile.DeliveryState); Assert.AreEqual("", hostile.Query);
        var expected = $"Mağaza: etsy · S1 · {p.FromUtc!.Value.ToString("d", CultureInfo.CurrentCulture)}–{p.ToUtc!.Value.ToString("d", CultureInfo.CurrentCulture)} · Durum: Yolda · Arama: kupa";
        Assert.AreEqual(expected, ReportParameters.Summary(p, ReportParameters.SchemaFor(Def("orders-csv"))));
        StringAssert.EndsWith(ReportParameters.Summary(p, ReportParameters.SchemaFor(Def("orders-csv")), includeQuery: false), "Durum: Yolda · Arama: var");
        Assert.AreEqual("", ReportParameters.Summary(new("support-package"), ReportParameters.SchemaFor(Def("support-package"))));
        Assert.AreEqual("Mağaza: seçilmedi · Durum: tümü", ReportParameters.Summary(new("orders-csv"), ReportParameters.SchemaFor(Def("orders-csv"))));
    }

    [TestMethod]
    public async Task TheOrdersRunnerQueriesOnlyTheStoreRangeAndStateExportsTheChosenColumnsAndRecordsEveryOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
            static OrderSnapshot Order(string marketplace, string shop, string id, DateTimeOffset updated, string? state, decimal total) => new()
            {
                Marketplace = marketplace, ShopId = shop, OrderId = id, UpdatedAt = updated, Total = total, Currency = "USD",
                Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }],
                Shipments = state is null ? [] : [new() { Id = "P-" + id, Carrier = "Aras", TrackingNumber = "TRK0000000" + id, State = state }],
            };
            orders.SaveManual(Order("etsy", "S1", "1001", now.AddDays(-2), "InTransit", 12.5m));
            orders.SaveManual(Order("etsy", "S1", "1002", now.AddDays(-5), "Delivered", 7m));
            orders.SaveManual(Order("etsy", "S1", "1003", now.AddDays(-60), "InTransit", 3m));
            orders.SaveManual(Order("etsy", "S1", "1004", now.AddDays(-1), null, 4m));
            orders.SaveManual(Order("ebay", "E1", "2001", now.AddDays(-1), "InTransit", 9m));
            var def = Def("orders-csv"); var allowed = new[] { "etsy|S1", "ebay|E1" };
            var p = ReportParameters.Defaults(def, allowed, DateTime.UtcNow);
            var path = Path.Combine(root, "out", "orders.csv"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var defaultColumns = ReportColumns.Default(ReportColumns.OrdersSchema).VisibleKeys;

            var query = await ReportRunner.QueryAsync(root, def, p, allowed);
            Assert.AreEqual(ReportRunState.Succeeded, query.State); Assert.AreEqual(3, query.Result!.Rows.Count, "Two shipped orders and the one without a package, all in the last 30 days of this store.");
            Assert.IsTrue(query.Result.Rows.All(r => r["ShopId"] is "S1" && ((string)r["OrderId"]!).StartsWith("100", StringComparison.Ordinal))); Assert.IsFalse(query.Result.Rows.Any(r => r["OrderId"] is "1003"), "Not the order outside the range.");
            var row1001 = query.Result.Rows.Single(r => r["OrderId"] is "1001");
            Assert.AreEqual("Yolda", row1001["Status"]); Assert.AreEqual(12.5m, row1001["Price"]); Assert.AreEqual("TR••••••1001", row1001["Tracking"], "The tracking column is masked in the rows themselves.");
            Assert.AreEqual("", query.Result.Rows.Single(r => r["OrderId"] is "1004")["Tracking"], "No package, no tracking.");

            var export = await ReportRunner.ExportAsync(root, query.Result, defaultColumns, path);
            Assert.AreEqual(ReportRunState.Succeeded, export.State); Assert.AreEqual(3, export.Rows);
            var lines = File.ReadAllLines(path);
            Assert.AreEqual("OrderId;ShopId;Status;Price;Currency;UpdatedUtc", lines[0]); Assert.AreEqual(4, lines.Length);
            StringAssert.Contains(lines.Single(l => l.StartsWith("1001", StringComparison.Ordinal)), ";Yolda;12.5;USD;"); Assert.IsFalse(File.ReadAllText(path).Contains("TRK0000000"), "The default export carries no tracking.");
            Assert.IsFalse(Directory.GetFiles(Path.GetDirectoryName(path)!).Any(f => f.Contains(".tmp-", StringComparison.Ordinal)), "No temp file is left behind.");
            var chosen = await ReportRunner.ExportAsync(root, query.Result, new[] { "Tracking", "OrderId" }, path, overwrite: true); // #880: replacing the earlier export is asked for
            Assert.AreEqual(3, chosen.Rows); Assert.AreEqual("Tracking;OrderId", File.ReadAllLines(path)[0]); StringAssert.Contains(File.ReadAllText(path), "TR••••••1001;1001");

            Assert.AreEqual(1, (await ReportRunner.QueryAsync(root, def, p with { DeliveryState = "InTransit" }, allowed)).Result!.Rows.Count);
            Assert.AreEqual(1, (await ReportRunner.QueryAsync(root, def, p with { DeliveryState = "Unknown" }, allowed)).Result!.Rows.Count, "'Unknown' also means an order without a package.");
            Assert.AreEqual(1, (await ReportRunner.QueryAsync(root, def, p with { Query = "1002" }, allowed)).Result!.Rows.Count);
            var empty = await ReportRunner.QueryAsync(root, def, p with { FromUtc = DateTime.UtcNow.Date.AddDays(-300), ToUtc = DateTime.UtcNow.Date.AddDays(-200) }, allowed);
            Assert.AreEqual(0, empty.Result!.Rows.Count); StringAssert.Contains(empty.Message, "sipariş yok");
            var emptyExport = await ReportRunner.ExportAsync(root, empty.Result, defaultColumns, path, overwrite: true);
            StringAssert.Contains(emptyExport.Message, "yalnız başlık"); Assert.AreEqual(1, File.ReadAllLines(path).Length);

            var runs = new ReportRunStore(root).Recent("orders-csv");
            Assert.AreEqual(8, runs.Count, "Five queries and three exports."); Assert.IsTrue(runs.All(r => r.State == ReportRunState.Succeeded && r.StoreKey == "etsy|S1"));
            StringAssert.Contains(runs[0].Note, "Mağaza: etsy · S1"); StringAssert.Contains(runs[0].Note, "CSV: 6 kolon"); Assert.IsTrue(runs.Any(r => !r.Note.Contains("CSV")), "A query run has no export note.");

            // Guards: a store not offered, an inverted range, a report the workspace does not run, a non-CSV path, a foreign column, no column -- none records a run.
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ReportRunner.QueryAsync(root, def, p with { StoreKey = "ebay|E1" }, new[] { "etsy|S1" }));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ReportRunner.QueryAsync(root, def, p with { FromUtc = DateTime.UtcNow.Date, ToUtc = DateTime.UtcNow.Date.AddDays(-1) }, allowed));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ReportRunner.QueryAsync(root, Def("orders"), p, allowed));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => ReportRunner.ExportAsync(root, query.Result, defaultColumns, Path.Combine(root, "x.txt")));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => ReportRunner.ExportAsync(root, query.Result, new[] { "OrderId", "CustomerName" }, path));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => ReportRunner.ExportAsync(root, query.Result, Array.Empty<string>(), path));
            Assert.AreEqual(8, new ReportRunStore(root).Recent("orders-csv").Count);

            // Cancelled before the query: no result, the run says so; cancelled before the export: nothing written.
            using var cts = new CancellationTokenSource(); cts.Cancel();
            var cancelledQuery = await ReportRunner.QueryAsync(root, def, p, allowed, cts.Token);
            Assert.AreEqual(ReportRunState.Cancelled, cancelledQuery.State); Assert.IsNull(cancelledQuery.Result);
            var other = Path.Combine(root, "out", "cancelled.csv");
            var cancelledExport = await ReportRunner.ExportAsync(root, query.Result, defaultColumns, other, cts.Token);
            Assert.AreEqual(ReportRunState.Cancelled, cancelledExport.State); Assert.IsFalse(File.Exists(other)); Assert.AreEqual(ReportRunState.Cancelled, new ReportRunStore(root).Latest()["orders-csv"].State);

            // A write that cannot land (the target is a directory) is a Failed run with a sentence, and no temp file stays.
            var blocked = Path.Combine(root, "out", "dir.csv"); Directory.CreateDirectory(blocked);
            var failed = await ReportRunner.ExportAsync(root, query.Result, defaultColumns, blocked);
            Assert.AreEqual(ReportRunState.Failed, failed.State); StringAssert.StartsWith(failed.Message, "Rapor yazılamadı"); Assert.AreEqual(ReportRunState.Failed, new ReportRunStore(root).Latest()["orders-csv"].State);
            Assert.IsFalse(Directory.GetFiles(Path.Combine(root, "out")).Any(f => f.Contains(".tmp-", StringComparison.Ordinal)));

            // #880: without asking, an existing file is never replaced — the refusal is a Failed run that names the file and never the directory, and the file stays as it was.
            var refused = await ReportRunner.ExportAsync(root, query.Result, defaultColumns, path);
            Assert.AreEqual(ReportRunState.Failed, refused.State); StringAssert.Contains(refused.Message, "üzerine yazılmadı"); Assert.IsFalse(refused.Message.Contains(root, StringComparison.OrdinalIgnoreCase), "never the directory");
            Assert.AreEqual(1, File.ReadAllLines(path).Length, "the existing header-only file is intact"); Assert.AreEqual(ReportRunState.Failed, new ReportRunStore(root).Latest()["orders-csv"].State);

            // The renderer's allow-list keeps customer fields out of any report template; the tracking column is the masked one.
            Assert.ThrowsException<InvalidOperationException>(() => ReportTemplateRenderer.Render(new ReportTemplate("orders", new[] { "OrderId", "CustomerName" }), Array.Empty<IReadOnlyDictionary<string, object?>>()));
            Assert.IsTrue(ReportTemplateRenderer.IsAllowed("Tracking") && !ReportTemplateRenderer.IsAllowed("CustomerName") && !ReportTemplateRenderer.IsAllowed("Email"));
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
