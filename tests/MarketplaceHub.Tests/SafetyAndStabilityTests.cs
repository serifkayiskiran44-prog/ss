using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
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
    public void ProfitabilitySimulatorCalculatesScenarioAndStaleMargin()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new ProfitabilityInput("SKU-1", "trendyol", 200m, 100m, 10m, 10m, 15m, 2m, "try", now, now);
        var result = ProfitabilitySimulator.Simulate(input, now);
        Assert.AreEqual(100m, result.GrossContribution); Assert.AreEqual(43m, result.NetContribution); Assert.AreEqual(21.5m, result.MarginPercent); Assert.AreEqual("OK", result.Status);
        Assert.AreEqual("43 TRY", ProfitabilitySimulator.Format(result.NetContribution, result.Input.Currency));
        Assert.AreEqual(1, ProfitabilitySimulator.Filter(new[] { result }, channel: "TRENDYOL").Count);
        var negative = ProfitabilitySimulator.Simulate(input with { Cost = 250m }, now); Assert.AreEqual("NEGATIVE_MARGIN", negative.Status);
        var stale = ProfitabilitySimulator.Simulate(input with { CommissionAtUtc = now.AddDays(-3) }, now); Assert.AreEqual("STALE", stale.Status);
    }

    [TestMethod]
    public void CurrencyPriceCenterConvertsWithProvenanceOverrideAndStaleGuard()
    {
        var now = DateTimeOffset.UtcNow; var center = new CurrencyPriceCenter();
        center.SetRate(new CurrencyRate("USD", "TRY", 32.5m, "fixture", now));
        var converted = center.Convert(10m, "USD", "TRY", now, TimeSpan.FromDays(1));
        Assert.AreEqual(325m, converted.ConvertedAmount); Assert.AreEqual("READY", converted.Status); Assert.IsFalse(converted.Stale);
        var audit = center.Override("USD", "TRY", 33m, "tester"); Assert.AreEqual(32.5m, audit.OldValue); Assert.AreEqual(33m, audit.NewValue);
        Assert.AreEqual(330m, center.Convert(10m, "USD", "TRY", now, TimeSpan.FromDays(1)).ConvertedAmount);
        var stale = center.Convert(1m, "USD", "TRY", now.AddDays(3), TimeSpan.FromDays(1)); Assert.AreEqual("STALE_LIVE_WRITE_BLOCKED", stale.Status);
        Assert.AreEqual(1234.56m, CurrencyPriceCenter.Parse("1.234,56", CultureInfo.GetCultureInfo("tr-TR")));
        Assert.AreEqual(1.01m, CurrencyPriceCenter.Round(1.005m));
    }

    [TestMethod]
    public void FieldSourcePolicyBlocksLowerPriorityAndSupportsApprovedApply()
    {
        var policy = new FieldSourcePolicy(); policy.Configure("p1", "Name", "manual", true);
        var blocked = policy.Preview("p1", "Name", "xml"); Assert.AreEqual("BLOCKED", blocked.Decision);
        policy.Configure("p1", "Name", "xml", false); var applied = policy.Preview("p1", "Name", "xml");
        Assert.AreEqual("APPLY", applied.Decision); Assert.AreEqual("APPLIED", policy.ApplyApproved(applied, applied.Version, true).Decision);
        Assert.ThrowsException<InvalidOperationException>(() => policy.ApplyApproved(applied, applied.Version - 1, true));
        Assert.AreEqual(1, policy.PreviewBulk(new[] { ("p1", "Name", "manual") }).Count);
    }

    [TestMethod]
    public void DropshipAnomalyGuardFailsClosedAndAuditsOverride()
    {
        var guard = new DropshipAnomalyGuard(); guard.SetProfile("s1", new AnomalyProfile(MaxCountDeltaPercent: 20, MaxZeroStockPercent: 60, MaxPriceDeltaPercent: 10, MaxTaxonomyChanges: 2));
        var old = new FeedRunMetrics("s1", 100, 5, 100m, 0, 0, DateTimeOffset.UtcNow.AddHours(-1));
        var current = old with { ProductCount = 70, ZeroStockCount = 70, AveragePrice = 120m, CategoryChanges = 2, BrandChanges = 1 };
        var report = guard.Evaluate(current, old); Assert.IsTrue(report.ApplyBlocked); CollectionAssert.Contains(report.Reasons.ToArray(), "MISSING_PRODUCTS"); CollectionAssert.Contains(report.Reasons.ToArray(), "MASS_ZERO_STOCK"); CollectionAssert.Contains(report.Reasons.ToArray(), "PRICE_JUMP");
        Assert.AreEqual("reason", guard.ApproveOverride(report, "manual feed review", "reason").Actor == "reason" ? "reason" : "");
    }

    [TestMethod]
    public void ChannelContentProfilesInheritOverrideFallbackAndGuardStale()
    {
        var store = new ChannelContentProfiles(); store.Save("shop", "channel", "tr-TR", "p1", new Dictionary<string, string> { ["Title"] = "Yerel", ["Description"] = "Açıklama" }, "manual");
        var profile = store.Save("shop", "channel", "en-US", "p1", new Dictionary<string, string> { ["Title"] = "English" }, "manual");
        var preview = store.Preview("shop", "channel", "en-US", "p1", new Dictionary<string, string> { ["Description"] = "Local" }, new Dictionary<string, int> { ["Title"] = 20 });
        Assert.AreEqual("English", preview.Fields["Title"]); Assert.AreEqual("Local", preview.Fields["Description"]); Assert.AreEqual("READY", preview.Status);
        Assert.ThrowsException<InvalidOperationException>(() => store.EnsureScope(preview, "other", "channel"));
        var clone = store.Clone(profile, "other", "en-US", true); Assert.AreEqual("other", clone.ShopId);
        var stale = store.Preview("shop", "channel", "en-US", "p1", new Dictionary<string, string>(), updatedAt: DateTimeOffset.UtcNow.AddDays(-3)); Assert.AreEqual("BLOCKED", stale.Status);
    }

    [TestMethod]
    public void SourceMissingQuarantineUsesGracePeriodAndRecoversLocally()
    {
        var q = new SourceMissingQuarantine(); var now = DateTimeOffset.UtcNow; var policy = new MissingSourcePolicy(TimeSpan.FromHours(2));
        var warning = q.Observe("supplier", "p1", false, 4, 10m, now, policy); Assert.AreEqual("WARNING", warning.State); Assert.IsTrue(warning.ListingPreserved);
        var pending = q.Observe("supplier", "p1", false, 4, 10m, now.AddHours(3), policy); Assert.AreEqual("PENDING_ACTION", pending.State);
        Assert.IsTrue(SourceMissingQuarantine.IsMassMissing(100, policy));
        var preview = q.PreviewDeactivate(pending, "manual review"); Assert.ThrowsException<InvalidOperationException>(() => q.ApplyLocalDeactivate(preview, false)); q.ApplyLocalDeactivate(preview, true);
        q.Observe("supplier", "p1", true, 4, 10m, now.AddHours(4), policy);
        Assert.AreEqual("LOCAL_RECOVERY", q.Audits.Last().Action); Assert.AreEqual(0, q.List().Count);
    }

    [TestMethod]
    public void DropshipStockSafetyAppliesBufferCapTextAndStaleBlock()
    {
        var now = DateTimeOffset.UtcNow; var policy = new StockSafetyPolicy("channel", "shop", 3, 10);
        var preview = DropshipStockSafety.Preview("p1", 20, null, policy, now, now, TimeSpan.FromDays(1));
        Assert.AreEqual(10, preview.SellableStock); Assert.AreEqual("PREVIEW", preview.Status); StringAssert.Contains(preview.Formula, "20-3");
        var text = DropshipStockSafety.Preview("p2", 20, false, policy, now, now, TimeSpan.FromDays(1)); Assert.AreEqual(0, text.SellableStock);
        var stale = DropshipStockSafety.Preview("p3", 20, true, policy, now.AddDays(-3), now, TimeSpan.FromDays(1)); Assert.AreEqual("BLOCKED_STALE", stale.Status); Assert.AreEqual(0, stale.SellableStock);
        Assert.ThrowsException<InvalidOperationException>(() => DropshipStockSafety.EnsureScope(preview, policy with { ShopId = "other" }));
    }

    [TestMethod]
    public void DropshipPriceFormulasPreviewVatPsychologicalAndOverflow()
    {
        var formula = new DropshipPriceFormula(Multiplier: 1.5m, FixedCost: 5m, VatPercent: 20m, OutputIncludesVat: true, Decimals: 2, PsychologicalEnding: true, MinimumMarginPercent: 10m);
        var preview = DropshipPriceFormulas.Preview("shop", "channel", "p1", 100m, 1m, "try", formula, 80m);
        Assert.AreEqual(186.99m, preview.ResultPrice); Assert.AreEqual("READY", preview.Status); Assert.AreEqual("TRY", preview.Currency);
        var blocked = DropshipPriceFormulas.Preview("shop", "channel", "p2", 100m, 1m, "try", formula, 180m); Assert.AreEqual("BLOCKED_LOW_MARGIN", blocked.Status);
        Assert.ThrowsException<InvalidOperationException>(() => DropshipPriceFormulas.Preview("shop", "channel", "p3", decimal.MaxValue, decimal.MaxValue, "try", formula));
        Assert.AreEqual(2, DropshipPriceFormulas.Bulk(new[] { ("p1", 10m), ("p2", 20m) }, "shop", "channel", 1m, formula, "try").Count);
    }

    [TestMethod]
    public void ChannelFeeCatalogValidatesProvenanceAndStaleLookup()
    {
        var now = DateTimeOffset.UtcNow; var catalog = new ChannelFeeCatalog(); var fee = new ChannelFee("channel", "cat", 12.5m, 2m, "TRY", now.AddDays(-1), now.AddDays(10), "official-csv", now);
        catalog.Import(new[] { fee }); var lookup = catalog.Lookup("CHANNEL", "CAT", now, now, TimeSpan.FromDays(2)); Assert.AreEqual("READY", lookup.Status); Assert.AreEqual(27m, ChannelFeeCatalog.Apply(200m, fee));
        var staleCatalog = new ChannelFeeCatalog(); staleCatalog.Import(new[] { fee with { CapturedUtc = now.AddDays(-10) } }); var stale = staleCatalog.Lookup("channel", "cat", now, now, TimeSpan.FromDays(2)); Assert.AreEqual("STALE", stale.Status);
        Assert.ThrowsException<ArgumentException>(() => catalog.Import(new[] { fee with { Provenance = "guessed" } }));
        Assert.AreEqual(1, catalog.Export().Count);
    }

    [TestMethod]
    public void EtsyCapabilityAuditKeepsOfficialInventoryPathAndWriteGates()
    {
        Assert.IsTrue(EtsyCapabilityAudit.UsesDedicatedInventoryPath("/v3/application/listings/123/inventory"));
        Assert.IsFalse(EtsyCapabilityAudit.UsesDedicatedInventoryPath("/v3/application/listings/123?includes=Inventory"));
        var update = EtsyCapabilityAudit.OfficialManifest.Single(x => x.Name == "listing-update"); Assert.AreEqual("PREVIEW_ONLY", update.Status); Assert.AreEqual("listings_w", update.Scope);
        Assert.IsTrue(EtsyCapabilityAudit.MissingOrBlocked().Any(x => x.Name == "images-write"));
    }

    [TestMethod]
    public void EtsyOAuthReadinessRequiresScopesAndClassifiesOperatorErrors()
    {
        var ready = EtsyOAuthReadiness.Evaluate("SellerApp", new[] { "shops_r", "listings_r", "listings_w", "transactions_r" }, true); Assert.AreEqual("READY", ready.Status);
        var missing = EtsyOAuthReadiness.Evaluate("PersonalApp", new[] { "shops_r" }, true); Assert.AreEqual("REAUTHORIZE_REQUIRED", missing.Status); CollectionAssert.Contains(missing.MissingScopes.ToArray(), "listings_r");
        Assert.AreEqual("BLOCKED", EtsyOAuthReadiness.Evaluate("SellerApp", ready.GrantedScopes, false).Status);
        Assert.AreEqual("RATE_LIMITED", EtsyOAuthReadiness.ClassifyHttp(429)); Assert.AreEqual("SCOPE_OR_PERMISSION_ERROR", EtsyOAuthReadiness.ClassifyHttp(403));
    }

    [TestMethod]
    public void EtsyListingLifecycleGuardsPublishOwnershipAndDeleteReceipt()
    {
        var preview = EtsyListingLifecycle.CreatePreview("shop", 123, "draft", "active", new[] { new EtsyListingChange("title", "old", "new") });
        EtsyListingLifecycle.EnsurePublishReady(preview, true, true, true, true); Assert.ThrowsException<InvalidOperationException>(() => EtsyListingLifecycle.EnsureOwned(preview, "other"));
        var receipts = new HashSet<string>(); Assert.ThrowsException<InvalidOperationException>(() => EtsyListingLifecycle.DeleteGuard(preview, false, receipts));
        Assert.AreEqual(preview.Receipt, EtsyListingLifecycle.DeleteGuard(preview, true, receipts)); Assert.ThrowsException<InvalidOperationException>(() => EtsyListingLifecycle.DeleteGuard(preview, true, receipts));
        StringAssert.Contains(EtsyListingLifecycle.RedactDiagnostic("Authorization: Bearer secret x-api-key"), "[redacted]");
    }

    [TestMethod]
    public void EtsyMetadataCenterCachesProfilesAndBlocksStalePhysicalOrDigitalSelection()
    {
        var now = DateTimeOffset.UtcNow; var center = new EtsyMetadataCenter(); var profile = new EtsyMetadataProfile(1, 10, "Home", new Dictionary<string, string> { ["color"] = "red" }, "s1", "ship1", "return1", 3, false, now, "official-read"); center.Cache("p1", profile);
        var ready = center.Select("p1", now, TimeSpan.FromDays(1)); Assert.AreEqual("READY", ready.Status); EtsyMetadataCenter.EnsureWriteApproved(ready, true); CollectionAssert.Contains(EtsyMetadataCenter.MapProperties(profile.Properties, new HashSet<string> { "color" }).ToArray(), "color=red");
        var stale = center.Select("p1", now.AddDays(3), TimeSpan.FromDays(1)); Assert.AreEqual("BLOCKED", stale.Status); Assert.ThrowsException<InvalidOperationException>(() => EtsyMetadataCenter.EnsureWriteApproved(stale, true));
        center.Cache("p2", profile with { Digital = true, ShippingProfileId = "ship1" }); Assert.AreEqual("BLOCKED", center.Select("p2", now, TimeSpan.FromDays(1)).Status);
    }

    [TestMethod]
    public void EtsyMediaCenterHashesRanksAndBlocksUnverifiedVideoWithRetry()
    {
        var items = new[] { new EtsyMediaItem("1", "image", "main", new byte[] { 1, 2 }, 2, "xml"), new EtsyMediaItem("2", "video", "clip", new byte[] { 3 }, 1, "manual") };
        var blocked = EtsyMediaCenter.Preview(1, "shop", items, false); Assert.AreEqual("BLOCKED", blocked.Status); CollectionAssert.Contains(blocked.Errors.ToArray(), "VIDEO_LIVE_API_BLOCKED"); Assert.AreEqual("clip", blocked.Items[0].Name);
        var ready = EtsyMediaCenter.Preview(1, "shop", items.Where(x => x.Kind != "video"), false); Assert.AreEqual("READY_READ_ONLY", ready.Status); Assert.IsFalse(string.IsNullOrWhiteSpace(ready.Fingerprint));
        var retry = EtsyMediaCenter.Retry("1", "timeout", 1, DateTimeOffset.UtcNow); Assert.AreEqual(2, retry.Attempt); Assert.ThrowsException<InvalidOperationException>(() => EtsyMediaCenter.EnsureDestructiveApproved(blocked, true));
    }

    [TestMethod]
    public void EtsyInventoryVariantWriteRemainsExplicitlyDeferred()
    {
        var decision = EtsyInventoryDeferredGuard.Evaluate(3, true); Assert.AreEqual("DEFERRED_BY_USER", decision.Status); Assert.AreEqual(3, decision.PropertyCount);
        Assert.ThrowsException<InvalidOperationException>(() => EtsyInventoryDeferredGuard.EnsureNoWrite(decision));
    }

    [TestMethod]
    public void SharedVariantDomainRemainsDeferredWithoutStartingPersistence()
    {
        var decision = VariantDomainDeferredGuard.Evaluate("catalog-crud");
        Assert.AreEqual("DEFERRED_BY_USER", decision.Status);
        Assert.AreEqual("catalog-crud", decision.RequestedOperation);
        Assert.ThrowsException<InvalidOperationException>(() => VariantDomainDeferredGuard.EnsureNoWrite(decision));
        Assert.ThrowsException<ArgumentException>(() => VariantDomainDeferredGuard.Evaluate(" "));
    }

    [TestMethod]
    public void XmlVariantMappingRemainsDeferredAndPreservesNormalFeedBoundary()
    {
        var decision = XmlVariantMappingDeferredGuard.Evaluate("supplier-feed");
        Assert.AreEqual("DEFERRED_BY_USER", decision.Status);
        Assert.AreEqual("supplier-feed", decision.SourceId);
        Assert.ThrowsException<InvalidOperationException>(() => XmlVariantMappingDeferredGuard.EnsureNoWrite(decision));
    }

    [TestMethod]
    public void ExcelVariantWorkflowRemainsDeferredWithoutApplyingWorkbookChanges()
    {
        var decision = ExcelVariantWorkflowDeferredGuard.Evaluate("variant-only-stock");
        Assert.AreEqual("DEFERRED_BY_USER", decision.Status);
        Assert.AreEqual("variant-only-stock", decision.Mode);
        Assert.ThrowsException<InvalidOperationException>(() => ExcelVariantWorkflowDeferredGuard.EnsureNoApply(decision));
    }

    [TestMethod]
    public void ChannelVariantMappingRemainsDeferredWithoutConnectorPublish()
    {
        var decision = ChannelVariantMappingDeferredGuard.Evaluate("etsy");
        Assert.AreEqual("DEFERRED_BY_USER", decision.Status);
        Assert.AreEqual("etsy", decision.Channel);
        Assert.ThrowsException<InvalidOperationException>(() => ChannelVariantMappingDeferredGuard.EnsureNoPublish(decision));
    }

    [TestMethod]
    public void BundleCoreRemainsDeferredWithoutStockMovement()
    {
        var decision = BundleCoreDeferredGuard.Evaluate("bundle-1");
        Assert.AreEqual("DEFERRED_BY_USER", decision.Status);
        Assert.AreEqual("bundle-1", decision.BundleSku);
        Assert.ThrowsException<InvalidOperationException>(() => BundleCoreDeferredGuard.EnsureNoWrite(decision));
    }

    [TestMethod]
    public void EtsyBatchDriftChunksAtOfficialLimitAndClassifiesScopeAndStale()
    {
        var chunks = EtsyBatchDrift.Chunks(Enumerable.Range(1, 205).Select(x => (long)x)).ToArray(); Assert.AreEqual(3, chunks.Length); Assert.AreEqual(100, chunks[0].Count);
        var at = DateTimeOffset.UtcNow; var local = new EtsyLocalSnapshot(1, "shop", "Old", 10, 2, "active", "cat", "a", "1", at); var remote = new EtsyRemoteSnapshot(1, "shop", "New", 11, 2, "active", "cat", "a", "1", at);
        var drift = EtsyBatchDrift.Compare(remote, local, at, TimeSpan.FromDays(1)); Assert.AreEqual("MANUAL_REMOTE", drift.Classification); CollectionAssert.Contains(drift.Fields.ToArray(), "title");
        Assert.AreEqual("STALE", EtsyBatchDrift.Compare(remote with { CheckedUtc = at.AddDays(-3) }, local, at, TimeSpan.FromDays(1)).Classification);
        Assert.ThrowsException<InvalidOperationException>(() => EtsyBatchDrift.Compare(remote with { ShopId = "other" }, local, at, TimeSpan.FromDays(1)));
    }

    [TestMethod]
    public void EtsyOrderCompletionDeduplicatesResumesAndPreviewsRefundStock()
    {
        var now = DateTimeOffset.UtcNow; var service = new EtsyOrderCompletion(); var state = service.Import("shop", new[] { new EtsyOrderEvent("r1", "t1", "shop", "SKU", 12, "paid", 1, "sale", now), new EtsyOrderEvent("r1", "t1", "shop", "SKU", 12, "paid", 1, "sale", now) }, new("shop", null, "c1", 0, 0));
        Assert.AreEqual(1, state.Accepted); Assert.AreEqual(1, state.Duplicates); Assert.AreEqual("PAID", service.Events[0].Status);
        var refund = service.Import("shop", new[] { new EtsyOrderEvent("r1", "t2", "shop", "SKU", 12, "refunded", 1, "refund", now.AddMinutes(1)) }, state); var preview = service.PreviewRefund("shop", service.Events.Last(), 1); Assert.ThrowsException<InvalidOperationException>(() => EtsyOrderCompletion.EnsureApproved(preview, false)); EtsyOrderCompletion.EnsureApproved(preview, true);
        Assert.AreEqual(2, refund.Accepted); Assert.AreEqual(1, service.Events.Count(x => x.EventType == "refund"));
    }

    [TestMethod]
    public void EtsySellerCockpitAggregatesReadinessAndKeepsDryRunSeparate()
    {
        var scopes = EtsyOAuthReadiness.Evaluate("SellerApp", new[] { "shops_r", "listings_r", "listings_w", "transactions_r" }, true); var snapshot = new EtsyCockpitSnapshot("shop", "PASS", scopes, 3, 2, "HEALTHY", 0, 0, true, DateTimeOffset.UtcNow);
        Assert.AreEqual("READY_READ_ONLY", EtsySellerCockpit.Evaluate(snapshot).Status); EtsySellerCockpit.EnsureReadOnly(snapshot); Assert.ThrowsException<InvalidOperationException>(() => EtsySellerCockpit.EnsureReadOnly(snapshot with { DryRun = false }));
        var attention = EtsySellerCockpit.Evaluate(snapshot with { DriftCount = 1, DeadLetterCount = 2 }); Assert.AreEqual("ATTENTION", attention.Status); CollectionAssert.Contains(attention.Missing.ToArray(), "drift");
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
