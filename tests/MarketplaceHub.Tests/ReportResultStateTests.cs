using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #850 (DESIGN: Report empty and error states). A listed result, a store with no orders at all, orders the filters
// excluded, a failed query, a cancelled query and a result the column layout cannot show are six different states
// with their own words, severity and calls to action; the failure line on screen carries no path, no SQL, no
// connection string and no secret, and stays short.
[TestClass]
public sealed class ReportResultStateTests
{
    static readonly ReportDefinition Def = ReportCatalog.Find("orders-csv")!;
    static readonly ReportParameterSchema Schema = ReportParameters.SchemaFor(Def);
    static readonly ReportParameterSet Params = new("orders-csv", "etsy|S1", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    static readonly string[] Visible = { "OrderId", "ShopId" };
    static ReportResult Result(int rows, int storeTotal, params string[] keys) => new(Def, Params, Enumerable.Range(0, rows).Select(i => (IReadOnlyDictionary<string, object?>)keys.ToDictionary(k => k, k => (object?)$"{k}-{i}", StringComparer.Ordinal)).ToList(), DateTime.UtcNow, storeTotal);
    static string[] Keys(ReportResultStateModel m) => m.Actions.Select(a => a.Key).ToArray();

    [TestMethod]
    public void EachOutcomeHasItsOwnStateSeverityAndActions()
    {
        Assert.AreEqual(ReportResultKind.None, ReportResultStates.Compose(null, null, Schema, Visible).Kind);

        var ready = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(2, 2, "OrderId", "ShopId"), "2 sipariş listelendi."), Params, Schema, Visible);
        Assert.AreEqual(ReportResultKind.Ready, ready.Kind); Assert.IsTrue(ready.ShowsGrid); Assert.AreEqual(0, ready.Actions.Count); Assert.IsNull(ready.Primary);

        var trueEmpty = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(0, 0), "Aralıkta sipariş yok."), Params, Schema, Visible);
        Assert.AreEqual(ReportResultKind.TrueEmpty, trueEmpty.Kind); Assert.IsFalse(trueEmpty.ShowsGrid); StringAssert.Contains(trueEmpty.Title, "henüz sipariş yok");
        CollectionAssert.AreEqual(new[] { ReportResultStates.ActionOpenOrders, ReportResultStates.ActionRetry }, Keys(trueEmpty)); Assert.AreEqual(SeverityLevel.Info, trueEmpty.Level); Assert.AreEqual("Sipariş ekranına git", trueEmpty.Primary!.Label);

        var filtered = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(0, 12), ""), Params with { DeliveryState = "InTransit" }, Schema, Visible);
        Assert.AreEqual(ReportResultKind.FilteredEmpty, filtered.Kind); StringAssert.Contains(filtered.Text, $"{12:N0} sipariş var");
        CollectionAssert.AreEqual(new[] { ReportResultStates.ActionWidenRange, ReportResultStates.ActionClearState, ReportResultStates.ActionRetry }, Keys(filtered)); StringAssert.Contains(filtered.Primary!.Label, "90 güne");
        var filteredNoState = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(0, 12), ""), Params, Schema, Visible);
        CollectionAssert.AreEqual(new[] { ReportResultStates.ActionWidenRange, ReportResultStates.ActionRetry }, Keys(filteredNoState), "No state filter, no 'clear state' action.");
        var filteredNoDates = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(0, 3), ""), new("data-quality"), ReportParameters.SchemaFor(ReportCatalog.Find("data-quality")!), Visible);
        CollectionAssert.AreEqual(new[] { ReportResultStates.ActionRetry }, Keys(filteredNoDates), "A report without a date range offers no widening.");

        var failed = ReportResultStates.Compose(new(ReportRunState.Failed, null, @"Sorgu çalıştırılamadı: SQLite Error 14: 'unable to open database file' C:\Users\ali\orders.db token=abc123"), Params, Schema, Visible);
        Assert.AreEqual(ReportResultKind.QueryFailed, failed.Kind); Assert.AreEqual(SeverityLevel.Blocking, failed.Level);
        CollectionAssert.AreEqual(new[] { ReportResultStates.ActionRetry, ReportResultStates.ActionDiagnostics }, Keys(failed));
        Assert.IsFalse(failed.Text.Contains(@"C:\") || failed.Text.Contains("abc123"), failed.Text); StringAssert.Contains(failed.Text, "[yol]"); StringAssert.Contains(failed.Text, "unable to open database file");
        Assert.AreEqual(ReportResultKind.QueryFailed, ReportResultStates.Compose(new(ReportRunState.Succeeded, null, ""), Params, Schema, Visible).Kind, "A success without a result is a failure to show.");

        var cancelled = ReportResultStates.Compose(new(ReportRunState.Cancelled, null, "Sorgu iptal edildi."), Params, Schema, Visible);
        Assert.AreEqual(ReportResultKind.Cancelled, cancelled.Kind); Assert.AreEqual(SeverityLevel.Warning, cancelled.Level); CollectionAssert.AreEqual(new[] { ReportResultStates.ActionRetry }, Keys(cancelled)); Assert.AreEqual("Yeniden çalıştır", cancelled.Primary!.Label);

        var incompatible = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(3, 3, "Foo", "Bar"), ""), Params, Schema, Visible);
        Assert.AreEqual(ReportResultKind.SchemaIncompatible, incompatible.Kind); CollectionAssert.AreEqual(new[] { ReportResultStates.ActionResetColumns, ReportResultStates.ActionRetry }, Keys(incompatible)); StringAssert.Contains(incompatible.Text, "OrderId");
        Assert.AreEqual(ReportResultKind.Ready, ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(3, 3, "OrderId", "Foo"), ""), Params, Schema, Visible).Kind, "One known column is enough to show the grid.");
        var noVisible = ReportResultStates.Compose(new(ReportRunState.Succeeded, Result(3, 3, "OrderId"), ""), Params, Schema, Array.Empty<string>());
        Assert.AreEqual(ReportResultKind.SchemaIncompatible, noVisible.Kind); Assert.AreEqual("Görünür kolon yok.", noVisible.Text);
    }

    [TestMethod]
    public void TheScreenLineIsRedactedAndShort()
    {
        var line = ReportResultStates.SafeUiMessage(@"SQLite Error 1: 'no such table: orders' while SELECT payload FROM orders WHERE shop=$s; Data Source=C:\data\orders.db; password=hunter2");
        Assert.IsFalse(line.Contains(@"C:\") || line.Contains("hunter2") || line.Contains("SELECT payload"), line);
        StringAssert.Contains(line, "[sorgu]"); StringAssert.Contains(line, "[bağlantı]"); Assert.IsTrue(line.Length <= ReportResultStates.MaxUiMessageLength);
        Assert.AreEqual("Ayrıntı tanılama kayıtlarında.", ReportResultStates.SafeUiMessage(""));
        var longLine = ReportResultStates.SafeUiMessage(new string('x', 500));
        Assert.AreEqual(ReportResultStates.MaxUiMessageLength, longLine.Length); StringAssert.EndsWith(longLine, "…");
        var unix = ReportResultStates.SafeUiMessage("Rapor yazılamadı: /tmp/out/dir.csv is a directory");
        StringAssert.Contains(unix, "[yol]"); Assert.IsFalse(unix.Contains("/tmp"));
        Assert.AreEqual("Ürün silinmiş; yeni önizleme alınmalı.", ReportResultStates.SafeUiMessage("Ürün silinmiş; yeni önizleme alınmalı."), "Plain Turkish text passes untouched.");
    }
}
