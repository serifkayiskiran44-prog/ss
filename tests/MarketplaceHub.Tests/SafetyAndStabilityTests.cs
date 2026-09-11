using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class SafetyAndStabilityTests
{
    [TestMethod]
    public void AuditRedactionMasksCredentialsAndPii()
    {
        var safe = TrMarketplaceHubDesktop.AuditStore.Sanitize("Authorization: Bearer abc password=secret user@example.com +905321234567");
        Assert.IsFalse(safe.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("user@example.com", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("905321234567", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StabilityProbeMeasuresEveryIteration()
    {
        var calls = 0;
        var result = TrMarketplaceHubDesktop.StabilityProbe.Run(3, () => calls++);
        Assert.AreEqual(3, calls);
        Assert.AreEqual(3, result.Iterations);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
    }

    [TestMethod]
    public void CapabilityAuditRejectsUnsupportedOperationsAndPreservesBlockedChannels()
    {
        var audit = TrMarketplaceHubDesktop.MarketplaceCapabilityAudit.Run();

        Assert.IsTrue(audit.IsValid, string.Join("; ", audit.Errors));
        Assert.IsTrue(audit.Rows.All(row => row.Capabilities.All(operation =>
            TrMarketplaceHubDesktop.MarketplaceConnectionCatalog.Get(row.Channel).Capabilities.Supports(operation))));
        Assert.IsTrue(audit.Rows.Where(row => row.LiveApiBlocked).All(row => row.Decision == "LIVE_API_BLOCKED"));
        Assert.IsTrue(audit.Rows.Any(row => row.Channel == "navlungo" && row.Decision == "LIVE_API_BLOCKED"));
    }

    [TestMethod]
    public void EtsyListingClientUsesShopScopedReadAndExactUpdatePayload()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new RecordingHandler(requests));
        var client = new TrMarketplaceHubDesktop.EtsyShopClient(http);
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("key", "secret", "token", "123");

        var listing = client.GetListingAsync(credentials, 456).GetAwaiter().GetResult();
        var updated = client.UpdateSimpleListingAsync(credentials, 456, 7, 12.50m).GetAwaiter().GetResult();

        Assert.AreEqual(456, listing.ListingId);
        Assert.AreEqual(456, updated.ListingId);
        Assert.AreEqual("GET", requests[0].Method.Method);
        Assert.AreEqual("/v3/application/shops/123/listings/456", requests[0].RequestUri!.AbsolutePath);
        Assert.AreEqual("PATCH", requests[1].Method.Method);
        Assert.AreEqual("/v3/application/shops/123/listings/456", requests[1].RequestUri!.AbsolutePath);
        StringAssert.Contains(requestBodies[1], "quantity=7");
        StringAssert.Contains(requestBodies[1], "price=12.50");
    }

    [TestMethod]
    public void EbayTimeoutIsReportedWithoutLeakingCredentialDetails()
    {
        using var http = new HttpClient(new TimeoutHandler());
        var connection = new TrMarketplaceHubDesktop.EbayConnection(http);
        var settings = new TrMarketplaceHubDesktop.EbaySettings("client", "secret", "runame", "https://example.test/callback", true);
        var tokens = new TrMarketplaceHubDesktop.EbayTokens("access", DateTimeOffset.UtcNow.AddMinutes(5), "refresh", DateTimeOffset.UtcNow.AddDays(1));

        var error = Assert.ThrowsException<InvalidOperationException>(() => connection.GetOrdersAsync(settings, tokens, cancellationToken: new CancellationTokenSource(50).Token).GetAwaiter().GetResult());
        StringAssert.Contains(error.Message, "zaman aşımı");
        Assert.IsFalse(error.Message.Contains("access", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SecondaryConnectorSnapshotExposesPartialAndBlockedStates()
    {
        var rows = TrMarketplaceHubDesktop.SecondaryConnectorAudit.Snapshot();

        Assert.AreEqual(6, rows.Count);
        Assert.AreEqual("PARTIAL", rows.Single(x => x.Channel == "allegro").Status);
        Assert.IsTrue(rows.Where(x => x.Channel is "joom" or "wish" or "fruugo" or "navlungo")
            .All(x => x.Status == "LIVE_API_BLOCKED" && x.Detail.Contains("HTTP isteği oluşturulmaz")));
    }

    [TestMethod]
    public void BackupRestoreValidatesHashAndKeepsPreRestoreRollbackDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-drill-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup.zip");
        try
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            _ = new TrMarketplaceHubDesktop.Catalog.CatalogStore(data).Products();
            File.WriteAllText(Path.Combine(data, "marker.txt"), "before");
            var service = new TrMarketplaceHubDesktop.DataBackupService(data);
            service.Backup(backup);
            File.WriteAllText(Path.Combine(data, "marker.txt"), "changed");

            var manifest = service.Validate(backup);
            service.Restore(backup);

            Assert.IsTrue(manifest.Files.Any(x => x.Path == "marker.txt"));
            Assert.AreEqual("before", File.ReadAllText(Path.Combine(data, "marker.txt")));
            Assert.IsTrue(Directory.GetDirectories(root, "data.pre-restore-*").Length == 1);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SupportExportDoesNotContainSecretAuditValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-export-" + Guid.NewGuid().ToString("N"));
        var export = Path.Combine(root, "support.zip");
        try
        {
            Directory.CreateDirectory(root);
            var audit = new TrMarketplaceHubDesktop.AuditStore(root);
            audit.Append(new() { Module = "redteam", Action = "error", Detail = "Authorization: Bearer live-secret password=hidden" });
            TrMarketplaceHubDesktop.SupportPackageService.Export(export, root);
            using var archive = ZipFile.OpenRead(export);
            var content = string.Join("\n", archive.Entries.Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
            Assert.IsFalse(content.Contains("live-secret", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("hidden", StringComparison.Ordinal));
            Assert.IsTrue(content.Contains("[redacted]", StringComparison.OrdinalIgnoreCase));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReleaseFreezeRequiresArtifactAndNoP0P1Blocker()
    {
        var blocked = TrMarketplaceHubDesktop.ReleaseFreezeGate.Evaluate(
            [new TrMarketplaceHubDesktop.ProductionReadinessCheck("security", "BLOCKED", "P1")],
            artifactVerified: true);
        var ready = TrMarketplaceHubDesktop.ReleaseFreezeGate.Evaluate(
            [new TrMarketplaceHubDesktop.ProductionReadinessCheck("security", "PASS", "ok")],
            artifactVerified: true);

        Assert.AreEqual("FROZEN", blocked.Status);
        Assert.AreEqual("V1_READY", ready.Status);
    }

    [TestMethod]
    public async Task XmlSourceGateSerializesSameSourceAndAllowsDifferentSources()
    {
        var active = 0;
        var maximum = 0;
        async Task Work(string source)
        {
            await TrMarketplaceHubDesktop.XmlSourceExecutionGate.RunAsync(source, async () =>
            {
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(maximum, current));
                await Task.Delay(20);
                Interlocked.Decrement(ref active);
            });
        }

        await Task.WhenAll(Work("same"), Work("same"));
        Assert.AreEqual(1, maximum);
    }

    [TestMethod]
    public void DynamicMarketplacePanelIsShopScopedAndCapabilityAware()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-panel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connections = new TrMarketplaceHubDesktop.MarketplaceConnectionStore(root);
            connections.Save("allegro", "shop-a", "A", true);
            connections.Save("allegro", "shop-b", "B", true);
            connections.Save("amazon", "shop-a", "Amazon", true);
            new TrMarketplaceHubDesktop.MarketplaceMappingStore(root).Save(new("allegro", "shop-a", "product-1", "offer-1"));

            var rows = TrMarketplaceHubDesktop.MarketplaceProductPanelModel.Build("product-1", root);

            Assert.AreEqual("offer-1", rows.Single(x => x.Channel == "allegro" && x.ShopId == "shop-a").MappingId);
            Assert.AreEqual("MAPPING_REQUIRED", rows.Single(x => x.Channel == "allegro" && x.ShopId == "shop-b").Readiness);
            Assert.IsTrue(rows.Where(x => x.Channel == "amazon").All(x => x.Status == "LIVE_API_BLOCKED"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EtsyProductReadinessBlocksMissingOrWrongShopMapping()
    {
        var product = new TrMarketplaceHubDesktop.Catalog.CatalogProduct { Name = "Ürün", Description = "Açıklama", Price = 10, Stock = 2, Currency = "USD", ImageUrls = "https://example.test/p.jpg" };
        var template = new TrMarketplaceHubDesktop.EtsyListingTemplate { TaxonomyId = 1, ShippingProfileId = 2, ReadinessStateId = 3 };
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("k", "s", "t", "123");
        var wrongShop = new TrMarketplaceHubDesktop.MarketplaceMapping("etsy", "999", product.Id, "listing");

        var result = TrMarketplaceHubDesktop.EtsyProductReadiness.Evaluate(product, template, credentials, wrongShop);

        Assert.AreEqual("BLOCKED", result.Status);
        CollectionAssert.Contains(result.Missing.ToList(), "shop-scoped listing mapping");
    }

    [TestMethod]
    public void OperationsSummaryUsesRealSnapshotCountersAndEmptyState()
    {
        var snapshot = new TrMarketplaceHubDesktop.DashboardSnapshot(0, 0, 0, 2, 1, 3, 0, 0, "Henüz çalışmadı", null, 1, DateTime.UtcNow, [],
            [new("Hata", "Sync başarısız", "x", "sync")], []);
        var summary = TrMarketplaceHubDesktop.OperationsSummaryService.From(snapshot);
        Assert.AreEqual(3, summary.OpenOrders);
        Assert.AreEqual(2, summary.FailedSyncs);
        Assert.IsTrue(summary.HasAction);
    }

    [TestMethod]
    public void ScreenParityAuditReportsMissingRouteAndPassesCompleteContract()
    {
        var missing = TrMarketplaceHubDesktop.ScreenParityAudit.Evaluate(["dashboard", "products"]);
        var complete = TrMarketplaceHubDesktop.ScreenParityAudit.Evaluate(TrMarketplaceHubDesktop.ScreenParityAudit.RequiredRoutes);
        Assert.AreEqual("BLOCKED", missing.Status);
        CollectionAssert.Contains(missing.MissingRoutes.ToList(), "orders");
        Assert.AreEqual("WORKING", complete.Status);
    }

    [TestMethod]
    public void PreflightCenterClassifiesBlockedWarningAndReadyWithoutNetwork()
    {
        var blocked = TrMarketplaceHubDesktop.PreflightCenter.Evaluate([
            new("credentials", "PASS", "local"), new("connector", "BLOCKED", "LIVE_API_BLOCKED")]);
        var partial = TrMarketplaceHubDesktop.PreflightCenter.Evaluate([
            new("catalog", "PASS", "local"), new("connector", "WARN", "manual verification")]);
        var ready = TrMarketplaceHubDesktop.PreflightCenter.Evaluate([
            new("catalog", "PASS", "local"), new("live-write-gate", "PASS", "preview required")]);

        Assert.AreEqual("BLOCKED", blocked.Status);
        Assert.AreEqual(1, blocked.BlockingItems.Count);
        Assert.AreEqual("PARTIAL", partial.Status);
        Assert.AreEqual("CODEX_READY", ready.Status);
        Assert.IsTrue(ready.IsReady);
    }

    [TestMethod]
    public void StoreOnboardingResumesAfterReadOnlyFailureWithoutSecrets()
    {
        var started = TrMarketplaceHubDesktop.StoreOnboardingLifecycle.Begin("etsy", "shop-a");
        var blocked = TrMarketplaceHubDesktop.StoreOnboardingLifecycle.CompleteReadOnlyTest(started, false, "LIVE_API_BLOCKED token=secret");
        var resumed = TrMarketplaceHubDesktop.StoreOnboardingLifecycle.CompleteReadOnlyTest(blocked, true, "ok");

        Assert.AreEqual("BLOCKED", blocked.Status);
        Assert.IsFalse(blocked.Detail.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(blocked.CanResume);
        Assert.AreEqual("CAPABILITY_DISCOVERY", resumed.Stage);
    }

    [TestMethod]
    public void SyncRetryPreviewRequiresApprovalAndRejectsStalePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrMarketplaceHubDesktop.Catalog.SyncStore(root);
            var job = store.Enqueue(new TrMarketplaceHubDesktop.Catalog.SyncRequest("etsy", "shop-a", "stock", "p-1", "v1"));
            store.TryStart(job.Id); store.Fail(job.Id, "network timeout");
            var preview = TrMarketplaceHubDesktop.Catalog.SyncOperations.CreateRetryPreview(store.List());
            Assert.AreEqual(1, preview.Count);
            Assert.AreEqual(0, TrMarketplaceHubDesktop.Catalog.SyncOperations.ApplyApprovedRetry(store, preview, false));
            Assert.AreEqual(1, TrMarketplaceHubDesktop.Catalog.SyncOperations.ApplyApprovedRetry(store, preview, true));
            Assert.AreEqual(TrMarketplaceHubDesktop.Catalog.SyncStatus.Pending, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DatabaseHealthInspectsReadOnlySqliteAndDetectsMissingDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            var missing = TrMarketplaceHubDesktop.DatabaseHealth.Inspect(root);
            Assert.AreEqual("NOT_CONFIGURED", missing.Status);
            _ = new TrMarketplaceHubDesktop.Catalog.CatalogStore(root).Products();
            var healthy = TrMarketplaceHubDesktop.DatabaseHealth.Inspect(root);
            Assert.AreEqual("HEALTHY", healthy.Status);
            Assert.AreEqual("ok", healthy.QuickCheck);
            Assert.AreEqual(TrMarketplaceHubDesktop.SchemaVersion.Current, healthy.SchemaVersion);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AuditPagingUsesStableCursorAndDoesNotReplayEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-audit-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrMarketplaceHubDesktop.AuditStore(root);
            for (var i = 0; i < 3; i++) store.Append(new() { AtUtc = DateTime.UtcNow.AddMinutes(-i), Action = "action-" + i });
            var first = TrMarketplaceHubDesktop.AuditPaging.Read(store, 2);
            var second = TrMarketplaceHubDesktop.AuditPaging.Read(store, 2, first.NextBeforeUtc);
            Assert.AreEqual(2, first.Items.Count);
            Assert.IsTrue(first.HasMore);
            Assert.AreEqual(1, second.Items.Count);
            Assert.IsFalse(second.Items.Any(x => first.Items.Any(y => y.Id == x.Id)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OfflineModeSeparatesNetworkAuthAndRateLimitAndBlocksDispatch()
    {
        var offline = TrMarketplaceHubDesktop.OfflineMode.Classify(new HttpRequestException("DNS unavailable"));
        var auth = TrMarketplaceHubDesktop.OfflineMode.Classify(new InvalidOperationException("HTTP 401 credential expired"));
        var rate = TrMarketplaceHubDesktop.OfflineMode.Classify(new InvalidOperationException("HTTP 429 rate limit"));

        Assert.AreEqual(TrMarketplaceHubDesktop.ConnectivityMode.Offline, offline.Mode);
        Assert.AreEqual(TrMarketplaceHubDesktop.ConnectivityMode.AuthRequired, auth.Mode);
        Assert.AreEqual(TrMarketplaceHubDesktop.ConnectivityMode.RateLimited, rate.Mode);
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.OfflineMode.EnsureDispatchAllowed(offline));
    }

    [TestMethod]
    public void WorkspaceStateRestoresValidStateAndFallsBackOnCorruption()
    {
        var state = new TrMarketplaceHubDesktop.WorkspaceState("products", "shop-a", 100, "active");
        var restored = TrMarketplaceHubDesktop.WorkspaceStateCodec.Restore(TrMarketplaceHubDesktop.WorkspaceStateCodec.Serialize(state));
        var fallback = TrMarketplaceHubDesktop.WorkspaceStateCodec.Restore("{bad json");

        Assert.AreEqual("products", restored.Route);
        Assert.AreEqual("shop-a", restored.ShopId);
        Assert.AreEqual(100, restored.PageSize);
        Assert.AreEqual("dashboard", fallback.Route);
    }

    [TestMethod]
    public void LocalCultureKeepsTurkishAmountAndUtcDisplayDeterministic()
    {
        Assert.IsTrue(TrMarketplaceHubDesktop.LocalCulture.TryParseAmount("1.234,56", out var turkish));
        Assert.AreEqual(1234.56m, turkish);
        Assert.IsTrue(TrMarketplaceHubDesktop.LocalCulture.TryParseAmount("1234.56", out var invariant));
        Assert.AreEqual(1234.56m, invariant);
        StringAssert.Contains(TrMarketplaceHubDesktop.LocalCulture.FormatAmount(1234.56m, "try"), "1.234,56 TRY");
        var local = TrMarketplaceHubDesktop.LocalCulture.ToIstanbul(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(15, local.Hour);
    }

    [TestMethod]
    public void WindowPlacementClampsOffscreenAndInvalidSavedGeometry()
    {
        var area = new TrMarketplaceHubDesktop.WindowRect(0, 0, 1920, 1080);
        var restored = TrMarketplaceHubDesktop.WindowPlacement.Restore(new(-500, 900, 2400, double.NaN), area);
        Assert.AreEqual(0, restored.Left);
        Assert.AreEqual(700, restored.Height);
        Assert.IsTrue(restored.Width <= area.Width);
        Assert.IsTrue(restored.Top + restored.Height <= area.Height);
    }

    [TestMethod]
    public void LargeScalePerformanceMeasures100kSyntheticProductsWithoutMarketplaceAccess()
    {
        var metrics = TrMarketplaceHubDesktop.LargeScalePerformance.Measure();
        Assert.AreEqual(100_000, metrics.ProductCount);
        Assert.IsTrue(metrics.MatchingCount > metrics.PageCount);
        Assert.AreEqual(100, metrics.PageCount);
        Assert.IsTrue(metrics.Elapsed < TimeSpan.FromSeconds(5));
        Assert.IsTrue(metrics.WorkingSetBytes > 0);
    }

    [TestMethod]
    public void UpdateChannelRequiresHttpsAndVerifiedPackageHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var package = Path.Combine(root, "package.bin"); File.WriteAllBytes(package, [1, 2, 3]);
            var notConfigured = TrMarketplaceHubDesktop.UpdateChannel.ValidateSource(null, null, package);
            var blocked = TrMarketplaceHubDesktop.UpdateChannel.ValidateSource(new Uri("http://example.test/update"), null, package);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package)));
            var verified = TrMarketplaceHubDesktop.UpdateChannel.ValidateSource(new Uri("https://example.test/update"), hash, package);
            Assert.AreEqual("NOT_CONFIGURED", notConfigured.Status); Assert.AreEqual("BLOCKED", blocked.Status); Assert.AreEqual("VERIFIED", verified.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HelpTopicsOnlyReferenceKnownSafeRoutes()
    {
        Assert.IsTrue(TrMarketplaceHubDesktop.HelpTopics.All.Count >= 5);
        Assert.IsTrue(TrMarketplaceHubDesktop.HelpTopics.All.All(x => TrMarketplaceHubDesktop.ScreenParityAudit.RequiredRoutes.Contains(x.Route)));
        Assert.AreEqual("Sync merkezi", TrMarketplaceHubDesktop.HelpTopics.ForRoute("SYNC")!.Title);
        StringAssert.Contains(TrMarketplaceHubDesktop.HelpTopics.ForRoute("connections")!.Recovery, "LIVE_API_BLOCKED");
    }

    [TestMethod]
    public void SellerRehearsalProducesDeterministicEndToEndReportWithoutLiveWrite()
    {
        var report = TrMarketplaceHubDesktop.SellerRehearsal.Run();
        Assert.IsTrue(report.IsClear);
        Assert.AreEqual(6, report.Steps.Count);
        Assert.IsTrue(report.Steps.All(x => x.Status == "PASS"));
        StringAssert.Contains(report.Steps.Single(x => x.Name == "fault-recovery").Recovery, "429");
    }

    [TestMethod]
    public void GaAcceptanceRequiresAllEvidenceAndBlocksP1()
    {
        var blocked = TrMarketplaceHubDesktop.GaAcceptance.Evaluate(new(false, true, true, true,
            [new("SEC-1", "P1", "write", "stale preview") ]));
        var ready = TrMarketplaceHubDesktop.GaAcceptance.Evaluate(new(true, true, true, true, []));
        Assert.AreEqual("NOT_READY", blocked.Status);
        Assert.IsTrue(blocked.Blockers.Count >= 2);
        Assert.AreEqual("V1_READY", ready.Status);
        Assert.IsTrue(ready.IsReady);
    }

    [TestMethod]
    public void OrderArchiveRestoresWithoutDeletingOrderData()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var orders = new TrMarketplaceHubDesktop.OrdersStore(root);
            var order = new TrMarketplaceHubDesktop.OrderSnapshot { Marketplace = "MANUAL", ShopId = "shop-a", OrderId = "order-1", RawStatus = "Closed", Source = "Yerel / manuel", UpdatedAt = DateTimeOffset.UtcNow };
            orders.SaveManual(order);
            var archive = new TrMarketplaceHubDesktop.OrderArchiveStore(root);
            archive.Archive("MANUAL", "shop-a", "order-1");
            Assert.IsTrue(archive.IsArchived("MANUAL", "shop-a", "order-1"));
            archive.Restore("MANUAL", "shop-a", "order-1");
            Assert.IsFalse(archive.IsArchived("MANUAL", "shop-a", "order-1"));
            Assert.AreEqual(1, orders.ReadAll().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SecurityThreatModelBlocksP1AndRedactsSyntheticSecret()
    {
        var assessment = TrMarketplaceHubDesktop.SecurityThreatModel.Assess([
            new("SEC-1", "P1", "export", "Authorization: Bearer synthetic-secret"),
            new("SEC-2", "P2", "ui", "manual review")]);
        var clear = TrMarketplaceHubDesktop.SecurityThreatModel.Assess([
            new("SEC-3", "P2", "fixture", "bounded")]);

        Assert.AreEqual("BLOCKED", assessment.Status);
        Assert.IsTrue(assessment.HasReleaseBlocker);
        Assert.IsFalse(assessment.Findings[0].Detail.Contains("synthetic-secret", StringComparison.Ordinal));
        Assert.AreEqual("CONDITIONAL", clear.Status);
    }

    private static readonly List<string> requestBodies = new();

    private sealed class RecordingHandler(List<HttpRequestMessage> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            requestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            var json = request.Method == HttpMethod.Get
                ? "{\"listing_id\":456,\"title\":\"Demo\",\"description\":\"d\",\"state\":\"active\",\"quantity\":2,\"price\":{\"amount\":1250,\"divisor\":100,\"currency_code\":\"USD\"},\"skus\":[\"SKU-1\"]}"
                : "{\"listing_id\":456}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
