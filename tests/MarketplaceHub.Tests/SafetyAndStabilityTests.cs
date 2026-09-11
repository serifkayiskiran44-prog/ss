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
using TrMarketplaceHubDesktop;

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
    public void OrderTransferXmlRoundTripsInvariantMoneyAndRejectsMalformedRoot()
    {
        var rows = new[] { new TrMarketplaceHubDesktop.OrderTransferRow("MANUAL", "shop-a", "o-1", "Yerel / manuel", new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero), 1234.56m) };
        var xml = TrMarketplaceHubDesktop.OrderTransferCodec.Export(rows);
        var restored = TrMarketplaceHubDesktop.OrderTransferCodec.Import(xml);
        Assert.AreEqual(1234.56m, restored[0].Total);
        Assert.AreEqual("shop-a", restored[0].ShopId);
        Assert.ThrowsException<InvalidDataException>(() => TrMarketplaceHubDesktop.OrderTransferCodec.Import("<wrong/>"));
    }

    [TestMethod]
    public void UnmappedResolutionSuggestsButNeverAutoBindsAmbiguousOrWrongShop()
    {
        var record = new TrMarketplaceHubDesktop.UnmappedRecord("order", "etsy", "shop-a", "o-1", "SKU-1", "", "Demo", "Brand");
        var products = new[] { new TrMarketplaceHubDesktop.Catalog.CatalogProduct { Id = "p-1", Sku = "SKU-1", Name = "Demo", Brand = "Brand" } };
        var suggestion = TrMarketplaceHubDesktop.UnmappedResolution.Suggest(record, products).Single();
        Assert.AreEqual("SUGGESTED", suggestion.MatchKind);
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.UnmappedResolution.EnsureApproved(suggestion, "etsy", "shop-b"));
        TrMarketplaceHubDesktop.UnmappedResolution.EnsureApproved(suggestion, "etsy", "shop-a");
        Assert.AreEqual(suggestion.Fingerprint, TrMarketplaceHubDesktop.UnmappedResolution.Suggest(record, products).Single().Fingerprint);
    }

    [TestMethod]
    public void TrendyolPilotValidatesOfficialProductV2AndInvoicePreviewWithoutHttp()
    {
        TrMarketplaceHubDesktop.TrendyolPilot.ValidateProductBatch([new("869000000001", "SKU-1", "Ürün", "Marka", 3, 120, 100)]);
        TrMarketplaceHubDesktop.TrendyolPilot.ValidateInvoice(new(123, 456, "https://invoice.example.test/a.pdf", "TY42024567890123", 1700000000));
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.TrendyolPilot.ValidateProductBatch([new("b", "s", "t", "m", 1, 99, 100)]));
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.TrendyolPilot.ValidateInvoice(new(123, 456, "http://invoice.example.test/a", null, null)));
    }

    [TestMethod]
    public void InvoiceCenterGuardsScopeStalenessAndDuplicateAndClassifiesResponses()
    {
        var preview = InvoiceCenter.CreateTrendyolPreview(7, "shop-a", "order-1", 42, "https://example.test/invoice/1", "ABC2024000000001", 1700000000);
        Assert.AreEqual(preview.IdempotencyKey, InvoiceCenter.CreateTrendyolPreview(7, "shop-a", "order-1", 42, "https://example.test/invoice/1", "ABC2024000000001", 1700000000).IdempotencyKey);
        Assert.AreEqual("READY", InvoiceCenter.GuardDuplicate(new HashSet<string>(), preview).State);
        Assert.AreEqual("DUPLICATE", InvoiceCenter.GuardDuplicate(new HashSet<string> { preview.IdempotencyKey }, preview).State);
        Assert.ThrowsException<InvalidOperationException>(() => InvoiceCenter.EnsureOrderScope("shop-b", "order-1", preview));
        Assert.ThrowsException<InvalidOperationException>(() => InvoiceCenter.EnsureFresh(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));
        Assert.IsTrue(InvoiceCenter.ClassifyTrendyolResponse(503).ShouldRetry);
        Assert.AreEqual("AUTH_ERROR", InvoiceCenter.ClassifyTrendyolResponse(401).State);
        Assert.AreEqual("LIVE_API_BLOCKED", InvoiceCenter.DescribeProvider("FAST", true, false).Status);
    }

    [TestMethod]
    public async Task StockReconciliationDetectsDuplicateAndRequiresVersionedApproval()
    {
        var center = new StockReconciliationCenter();
        var movements = new[] { new StockMovement("m1", "shop", "trendyol", "p1", "order", -2, DateTimeOffset.UtcNow), new StockMovement("m1", "shop", "trendyol", "p1", "order", -2, DateTimeOffset.UtcNow) };
        var row = center.Reconcile("shop", "trendyol", "p1", 5, 10, movements);
        CollectionAssert.Contains(row.Causes.ToArray(), "duplicate-movement");
        var preview = center.PreviewCorrection(row, "sayım");
        Assert.ThrowsException<InvalidOperationException>(() => center.ApplyApproved(preview, 0, false));
        var audit = center.ApplyApproved(preview, 0, true);
        Assert.AreEqual("LOCAL_CORRECTION", audit.Action);
        var page = await center.PageAsync(new[] { row }, "p1", 0, 10);
        Assert.AreEqual(1, page.Count);
    }

    [TestMethod]
    public void ProductHistoryTracksProvenanceDiffAndStaleRollback()
    {
        var history = new ProductChangeHistory();
        history.Append("p1", "Name", null, "İlk", "import", "system", "token=secret");
        var second = history.Append("p1", "Name", "İlk", "Güncel", "manual", "user", "screen");
        Assert.AreEqual("[redacted]=[redacted]", history.Query("p1")[1].Context);
        Assert.IsTrue(history.Diff("p1", 1, 2).Single(x => x.Field == "Name").Changed);
        var preview = history.PreviewRollback("p1", "Name", 1);
        Assert.ThrowsException<InvalidOperationException>(() => history.ApplyLocalRollback(preview, 1, true));
        var applied = history.ApplyLocalRollback(preview, second.Version, true);
        Assert.AreEqual("rollback", applied.Actor);
        Assert.AreEqual("İlk", applied.NewValue);
    }

    [TestMethod]
    public async Task SupplierCostMonitorDetectsCurrencySafeThresholdAndStaleChanges()
    {
        var at = DateTimeOffset.UtcNow;
        var old = new SupplierCostSnapshot("s1", "xml", "p1", "Marka", "Kategori", 100m, "eur", at.AddHours(-1));
        var current = old with { Cost = 125m, CapturedUtc = at };
        var result = SupplierCostMonitor.Compare(current, old, 20, 10, TimeSpan.FromDays(1), at);
        Assert.AreEqual(25m, result.AmountDelta); Assert.AreEqual(25m, result.PercentDelta); Assert.IsTrue(result.Alert); Assert.IsFalse(result.Stale);
        Assert.AreEqual("125 EUR", SupplierCostMonitor.Format(current.Cost, current.Currency));
        var filtered = SupplierCostMonitor.Filter(new[] { result }, source: "XML", brand: "marka"); Assert.AreEqual(1, filtered.Count);
        var stale = SupplierCostMonitor.Compare(current with { CapturedUtc = at.AddDays(-3) }, old, 20, 10, TimeSpan.FromDays(1), at); Assert.AreEqual("STALE", stale.Status);
        var batch = await SupplierCostMonitor.BatchAsync(new[] { (current, old) }, 20, 10); Assert.AreEqual(1, batch.Count);
    }

    [TestMethod]
    public void PurchasePlanningCalculatesDraftAndSafeCsvExport()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new PurchasePlanningInput("SKU-1", "Marka", "Kategori", "Supplier, A", 10, 30, 5, 40, 12.5m, "eur", 4, now, now);
        var suggestion = PurchasePlanning.Calculate(input, now);
        Assert.AreEqual(1m, suggestion.DailyVelocity); Assert.AreEqual(10m, suggestion.DaysOfCover); Assert.AreEqual(30, suggestion.SuggestedQuantity); Assert.AreEqual("DRAFT_RECOMMENDATION", suggestion.Status);
        var csv = PurchasePlanning.ExportCsv(new[] { suggestion });
        StringAssert.Contains(csv, "SKU-1,\"Supplier, A\""); StringAssert.Contains(csv, "12.5,EUR");
        Assert.AreEqual(1, PurchasePlanning.Filter(new[] { suggestion }, supplier: "supplier, a").Count);
        var stale = PurchasePlanning.Calculate(input with { CostAtUtc = now.AddDays(-3) }, now); Assert.AreEqual("STALE", stale.Status);
    }

    [TestMethod]
    public void MultiLocationStockSeparatesVisibleTotalAndScopeAnomalies()
    {
        var locations = new[] { new StockLocation("a", "shop", "Ana", "warehouse"), new StockLocation("b", "shop", "Şube", "branch") };
        var now = DateTimeOffset.UtcNow;
        var rows = new[] { new LocationStock("a", "shop", "p", 10, "xml", now.AddMinutes(-2)), new LocationStock("b", "shop", "p", 5, "manual", now), new LocationStock("b", "shop", "p", 4, "manual", now.AddMinutes(-1)), new LocationStock("x", "other", "p", 99, "channel", now) };
        var view = MultiLocationStock.Summarize(locations, rows, "shop", "p", x => x.Id == "a");
        Assert.AreEqual(15, view.Total); Assert.AreEqual(10, view.Visible); Assert.AreEqual(0, view.NegativeLocations);
        CollectionAssert.Contains(view.Anomalies.ToArray(), "MULTIPLE_SNAPSHOTS");
        CollectionAssert.Contains(view.Anomalies.ToArray(), "WRONG_LOCATION_OR_SHOP");
        Assert.AreEqual(2, MultiLocationStock.Timeline(rows, "b", "shop", "p").Count);
    }

    [TestMethod]
    public void MetadataTemplatesApplyDefaultThenShopOverrideAndIgnoreStaleWrongChannel()
    {
        var templates = new[]
        {
            new TrMarketplaceHubDesktop.MetadataTemplate("etsy", "*", "cat", 1, new Dictionary<string,string>{{"brand","Default"},{"color","red"}}),
            new TrMarketplaceHubDesktop.MetadataTemplate("etsy", "shop-a", "cat", 2, new Dictionary<string,string>{{"brand","Shop A"}}),
            new TrMarketplaceHubDesktop.MetadataTemplate("etsy", "shop-a", "cat", 1, new Dictionary<string,string>{{"color","stale"}}, true),
            new TrMarketplaceHubDesktop.MetadataTemplate("ebay", "shop-a", "cat", 9, new Dictionary<string,string>{{"brand","Wrong"}})
        };
        var resolved = TrMarketplaceHubDesktop.MetadataTemplateResolver.Resolve("etsy", "shop-a", "cat", "p-1", templates);
        Assert.AreEqual("Shop A", resolved["brand"]);
        Assert.AreEqual("red", resolved["color"]);
    }

    [TestMethod]
    public void ImageHealthScannerClassifiesFailuresDuplicatesAndRedactsUrl()
    {
        var findings = TrMarketplaceHubDesktop.ImageHealthScanner.Evaluate([
            new("p-1", "https://cdn.example/a.jpg", "image/jpeg", 1000, 200),
            new("p-2", "https://cdn.example/a.jpg/", "image/jpeg", 1000, 200),
            new("p-3", "https://cdn.example/b", "text/html", 100, 200),
            new("p-4", "https://cdn.example/c", "image/png", 20_000_000, 200),
            new("p-5", "https://cdn.example/d", "image/png", 100, 404),
            new("p-6", "https://cdn.example/d?token=secret", "image/png", 100, 200)]);
        Assert.AreEqual("DUPLICATE", findings.Single(x => x.ProductId == "p-1").Status);
        Assert.AreEqual("WRONG_CONTENT", findings.Single(x => x.ProductId == "p-3").Status);
        Assert.AreEqual("OVERSIZE", findings.Single(x => x.ProductId == "p-4").Status);
        Assert.AreEqual("NOT_FOUND", findings.Single(x => x.ProductId == "p-5").Status);
        Assert.IsFalse(findings.Single(x => x.ProductId == "p-6").Url.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TransformRuleEngineHandlesCultureSafeRulesAndRowErrors()
    {
        var multiplied = TrMarketplaceHubDesktop.TransformRuleEngine.Apply("12.50", new("r1", 1, "multiply", Number: 2));
        var text = TrMarketplaceHubDesktop.TransformRuleEngine.Apply(" Ürün ", new("r2", 1, "trim"));
        var invalid = TrMarketplaceHubDesktop.TransformRuleEngine.Apply("not-number", new("r3", 1, "multiply", Number: 2));
        var unsupported = TrMarketplaceHubDesktop.TransformRuleEngine.Apply("x", new("r4", 1, "eval"));
        Assert.AreEqual("25", multiplied.Value); Assert.AreEqual("READY", multiplied.Status);
        Assert.AreEqual("Ürün", text.Value); Assert.AreEqual("ERROR", invalid.Status); Assert.AreEqual("ERROR", unsupported.Status);
    }

    [TestMethod]
    public void NotificationStoreDeduplicatesPersistsAndAcknowledgesRedactedEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-notify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrMarketplaceHubDesktop.NotificationStore(root);
            var first = store.Add("fp-1", "ERROR", "etsy", "shop-a", "Sync", "Authorization: Bearer synthetic-secret");
            var duplicate = store.Add("fp-1", "ERROR", "etsy", "shop-a", "Sync", "same");
            Assert.AreEqual(first.Id, duplicate.Id); Assert.IsFalse(store.List()[0].Acknowledged); Assert.IsFalse(store.List()[0].Detail.Contains("synthetic-secret", StringComparison.Ordinal));
            store.Acknowledge(first.Id); Assert.IsTrue(new TrMarketplaceHubDesktop.NotificationStore(root).List()[0].Acknowledged);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReturnManagementBlocksWrongShopMissingSkuAndStaleButAllowsPartialPreview()
    {
        var @case = new TrMarketplaceHubDesktop.ReturnCase("etsy", "shop-a", "order-1", "line-1", "SKU-1", 2, "customer", "OPEN", DateTimeOffset.UtcNow);
        var preview = TrMarketplaceHubDesktop.ReturnManagement.CreatePreview(@case, "shop-a", true, DateTimeOffset.Parse("2026-09-11T12:00:00Z"), DateTimeOffset.Parse("2026-09-11T12:00:00Z"));
        var wrongShop = TrMarketplaceHubDesktop.ReturnManagement.CreatePreview(@case, "shop-b", true, expectedOrderUpdated: DateTimeOffset.Parse("2026-09-11T12:00:00Z"), currentOrderUpdated: DateTimeOffset.Parse("2026-09-11T12:00:00Z"));
        Assert.AreEqual("PREVIEW_ONLY", preview.Status); Assert.AreEqual(2, preview.Quantity); Assert.AreEqual("BLOCKED", wrongShop.Status);
    }

    [TestMethod]
    public void CompetitionObserverScopesOffersDeduplicatesAndMarksStaleReadOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[] { new TrMarketplaceHubDesktop.OfferSnapshot("etsy", "shop-a", "p-1", "offer-1", 90, "USD", now, "fixture"), new("etsy", "shop-a", "p-1", "offer-1", 95, "USD", now.AddMinutes(1), "fixture"), new("etsy", "shop-b", "p-1", "offer-2", 80, "USD", now, "fixture"), new("etsy", "shop-a", "p-1", "offer-3", 110, "USD", now.AddDays(-2), "fixture") };
        var result = TrMarketplaceHubDesktop.CompetitionObserver.Evaluate(rows, "etsy", "shop-a", "p-1", 100, now);
        Assert.AreEqual(2, result.Count); Assert.AreEqual("OBSERVED", result.Single(x => x.OfferId == "offer-1").Status); Assert.AreEqual("STALE", result.Single(x => x.OfferId == "offer-3").Status); Assert.AreEqual(-5, result.Single(x => x.OfferId == "offer-1").Difference);
    }

    [TestMethod]
    public void ReportTemplateRendersFilteredAllowedColumnsAndRedactsSecrets()
    {
        var template = new TrMarketplaceHubDesktop.ReportTemplate("orders", ["OrderId", "ShopId", "Price", "Currency"]);
        var csv = TrMarketplaceHubDesktop.ReportTemplateRenderer.Render(template, [new Dictionary<string, object> { ["OrderId"] = "o-1", ["ShopId"] = "shop-a", ["Price"] = 12.5m, ["Currency"] = "USD", ["Token"] = "secret" }]);
        StringAssert.Contains(csv, "o-1;shop-a;12.5;USD"); Assert.IsFalse(csv.Contains("secret", StringComparison.Ordinal));
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.ReportTemplateRenderer.Render(new("orders", ["Token"]), []));
    }

    [TestMethod]
    public void ProductQualityScoreIsDeterministicAndExposesBlockedCapabilitySeparately()
    {
        var product = new TrMarketplaceHubDesktop.Catalog.CatalogProduct { Id = "p-1", Sku = "S", Barcode = "B", Name = "N", Description = "D", Price = 10, Stock = 1, ImageUrls = "https://x.test/a", Category = "C", Brand = "M", SourceId = "xml" };
        var score = TrMarketplaceHubDesktop.ProductQualityScoreCalculator.Calculate(product, "amazon", false);
        Assert.AreEqual(85, score.Score); Assert.AreEqual(TrMarketplaceHubDesktop.ProductQualityScoreCalculator.Version, score.Version);
        Assert.AreEqual("BLOCKED", score.Items.Single(x => x.Key == "channel").Status); Assert.AreEqual(1, score.FixList.Count);
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
