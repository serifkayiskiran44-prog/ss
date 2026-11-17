using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BackgroundAppControllerTests
{
    string root = "";

    [TestInitialize]
    public void CreateRoot() => root = Path.Combine(Path.GetTempPath(), "background-controller-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void DeleteRoot()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task StartIsIdempotentAndDoesNotCreateDuplicateScheduledRuns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var job = new DelegateBackgroundJob("orders", BackgroundJobAccess.RemoteReadOnly, async token =>
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await using var controller = new BackgroundAppController(root, [job], TimeSpan.FromMilliseconds(10));

        controller.Start();
        controller.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(80);

        Assert.AreEqual(1, Volatile.Read(ref runs));
        release.TrySetResult();
        await controller.ShutdownAsync();
    }

    [TestMethod]
    public async Task PauseSurvivesRestartAndResumeAllowsScheduledWork()
    {
        await using (var first = new BackgroundAppController(root, [], TimeSpan.FromMinutes(1)))
            first.Pause();

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reopened = new BackgroundAppController(root,
            [new DelegateBackgroundJob("orders", BackgroundJobAccess.RemoteReadOnly, _ => { ran.TrySetResult(); return Task.CompletedTask; })],
            TimeSpan.FromMilliseconds(20));
        Assert.AreEqual(BackgroundAppState.Paused, reopened.State);
        reopened.Start();
        await Task.Delay(80);
        Assert.IsFalse(ran.Task.IsCompleted);

        reopened.Resume();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await reopened.ShutdownAsync();
    }

    [TestMethod]
    public async Task RunNowIsolatesFailuresAndWritesOnlyRedactedBoundedHistory()
    {
        var successfulRuns = 0;
        const string secret = "token=super-secret-value";
        var jobs = new IBackgroundJob[]
        {
            new DelegateBackgroundJob("broken", BackgroundJobAccess.RemoteReadOnly, _ => throw new InvalidOperationException(secret)),
            new DelegateBackgroundJob("healthy", BackgroundJobAccess.RemoteReadOnly, _ => { successfulRuns++; return Task.CompletedTask; })
        };
        await using var controller = new BackgroundAppController(root, jobs, TimeSpan.FromMinutes(1));

        var result = await controller.RunNowAsync();

        Assert.AreEqual(1, successfulRuns);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Single(row => row.JobId == "broken").Succeeded == false);
        var audit = new AuditStore(root).List().Single(row => row.Module == "background" && row.Outcome == "Failed");
        Assert.IsFalse(audit.Detail.Contains("super-secret-value", StringComparison.Ordinal));
        Assert.IsTrue(audit.Detail.Length <= BackgroundAppController.MaxNotificationLength);
    }

    [TestMethod]
    public async Task ShutdownCancelsAndAwaitsActiveJobThenDisposesIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DelegateBackgroundJob("orders", BackgroundJobAccess.RemoteReadOnly, async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
        });
        await using var controller = new BackgroundAppController(root, [job], TimeSpan.FromMinutes(1));

        var run = controller.RunNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.ShutdownAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await run;
        Assert.IsTrue(job.IsDisposed);
        Assert.AreEqual(BackgroundAppState.Stopped, controller.State);
    }

    [TestMethod]
    public void RemoteWriteJobIsRejectedBeforeItsDelegateCanRun()
    {
        var invoked = false;
        var write = new DelegateBackgroundJob("remote-write", BackgroundJobAccess.RemoteWrite, _ => { invoked = true; return Task.CompletedTask; });

        Assert.ThrowsException<ArgumentException>(() => new BackgroundAppController(root, [write], TimeSpan.FromMinutes(1)));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public async Task ClosePolicyHidesAndNotifiesOnceWhileExplicitExitReallyExits()
    {
        await using var controller = new BackgroundAppController(root, [], TimeSpan.FromMinutes(1));
        controller.CloseToTray = true;

        Assert.AreEqual(new WindowCloseDecision(true, true, true), controller.DecideWindowClose());
        Assert.AreEqual(new WindowCloseDecision(true, true, false), controller.DecideWindowClose());
        controller.RequestExplicitExit();
        Assert.AreEqual(new WindowCloseDecision(false, false, false), controller.DecideWindowClose());
    }

    [TestMethod]
    public async Task CloseToTrayPreferenceSurvivesRestart()
    {
        await using (var first = new BackgroundAppController(root, [], TimeSpan.FromMinutes(1))) first.CloseToTray = false;
        await using var reopened = new BackgroundAppController(root, [], TimeSpan.FromMinutes(1));
        Assert.IsFalse(reopened.CloseToTray);
        Assert.AreEqual(new WindowCloseDecision(false, false, false), reopened.DecideWindowClose());
    }

    [TestMethod]
    public void OwnedAutomationLeaseCanBeReleasedWithoutTouchingANewerLease()
    {
        var store = new AutomationStore(root);
        var now = DateTime.UtcNow;
        var job = store.Save(new AutomationJob { Kind = AutomationKind.Health, NextRunUtc = now.AddMinutes(-1) });
        Assert.IsTrue(store.TryClaimLease(job.Id, now, TimeSpan.FromMinutes(5), out var token));

        Assert.IsFalse(store.ReleaseLease(job.Id, "wrong-token"));
        Assert.IsTrue(store.ReleaseLease(job.Id, token));
        Assert.IsTrue(store.TryClaimLease(job.Id, now, TimeSpan.FromMinutes(5), out _));
    }

    [TestMethod]
    public async Task ScheduledXmlJobImportsADueLocalFeedWithoutAWindow()
    {
        const string feed = "<Items><Item><sku>SKU-1</sku><name>Product</name><price>10</price><stock>4</stock></Item></Items>";
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "feed.xml");
        File.WriteAllText(feedPath, feed);
        var catalog = new CatalogStore(root);
        catalog.SaveSource(new XmlSource
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Local", Location = feedPath, Enabled = true, AutoImport = true,
            IntervalMinutes = 1, ItemPath = "/Items/Item", Currency = "TRY", MaximumStock = 100,
            Fields = new() { ["Sku"] = "sku", ["Name"] = "name", ["Cost"] = "price", ["Stock"] = "stock" }
        });
        using var job = new ScheduledXmlImportBackgroundJob(root, new HttpClient());

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, catalog.Products().Count);
        Assert.IsNotNull(catalog.Sources().Single().LastRunUtc);
        Assert.AreEqual(1, new XmlRunStore(root).List(catalog.Sources().Single().Id, 10).Count);
    }

    [TestMethod]
    public async Task ScheduledXmlFailureIsReportedAfterOtherSourcesStillRun()
    {
        const string feed = "<Items><Item><sku>SKU-OK</sku><name>Product</name><price>10</price><stock>4</stock></Item></Items>";
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "feed.xml");
        File.WriteAllText(feedPath, feed);
        var catalog = new CatalogStore(root);
        XmlSource Source(string id, string location) => new()
        {
            Id = id, Name = id, Location = location, Enabled = true, AutoImport = true, IntervalMinutes = 1,
            ItemPath = "/Items/Item", Currency = "TRY", MaximumStock = 100,
            Fields = new() { ["Sku"] = "sku", ["Name"] = "name", ["Cost"] = "price", ["Stock"] = "stock" }
        };
        catalog.SaveSource(Source(Guid.NewGuid().ToString("N"), "not-an-http-location"));
        catalog.SaveSource(Source(Guid.NewGuid().ToString("N"), feedPath));
        using var job = new ScheduledXmlImportBackgroundJob(root, new HttpClient());

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        Assert.IsTrue(catalog.Products().Any(product => product.Sku == "SKU-OK"));
        Assert.IsTrue(error.Message.Length <= BackgroundAppController.MaxNotificationLength);
    }

    [TestMethod]
    public async Task XmlAutomationExecutesItsGuardedSourceAndLeavesNoOrphanQueue()
    {
        const string feed = "<Items><Item><sku>SKU-AUTO</sku><name>Automated</name><price>12</price><stock>3</stock></Item></Items>";
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "automation-feed.xml");
        File.WriteAllText(feedPath, feed);
        var now = DateTime.UtcNow;
        var catalog = new CatalogStore(root);
        var source = new XmlSource
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Automation target", Location = feedPath, Enabled = true,
            AutoImport = false, IntervalMinutes = 60, ItemPath = "/Items/Item", Currency = "TRY", MaximumStock = 100,
            Fields = new() { ["Sku"] = "sku", ["Name"] = "name", ["Cost"] = "price", ["Stock"] = "stock" }
        };
        catalog.SaveSource(source);
        var automation = new AutomationStore(root);
        var xml = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = source.Id,
            NextRunUtc = now.AddMinutes(-1)
        });
        var stock = automation.Save(new AutomationJob { Kind = AutomationKind.Stock, NextRunUtc = now.AddMinutes(-1) });
        using var job = new AutomationReadQueueBackgroundJob(root, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.IsTrue(catalog.Products().Any(product => product.Sku == "SKU-AUTO"), "The due XML automation must execute its target, not merely enqueue metadata.");
        Assert.AreEqual(0, new SyncStore(root).List().Count, "A controller-owned automation must not leave an orphan SyncJob with no consumer.");
        Assert.IsTrue(automation.Get(xml.Id).NextRunUtc > now);
        Assert.IsNotNull(automation.Get(xml.Id).LastRunUtc);
        Assert.IsTrue(automation.Due(now).Any(row => row.Id == stock.Id), "A stock write plan must remain due and unclaimed for foreground approval.");
        Assert.IsNull(automation.Get(stock.Id).LockedUntilUtc);
    }

    [TestMethod]
    public async Task HealthAutomationInvokesExecutorBeforeAdvancingSchedule()
    {
        var now = DateTime.UtcNow;
        var automation = new AutomationStore(root);
        var health = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Health, TemplateKey = "health-check", Channel = "etsy", Shop = "123",
            NextRunUtc = now.AddMinutes(-1)
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(), () => now);
        using var executor = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, executor, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, executor.Calls);
        Assert.AreEqual(health.Id, executor.LastJobId);
        Assert.IsNotNull(automation.Get(health.Id).LastRunUtc);
        Assert.AreEqual(0, new SyncStore(root).List().Count);
    }

    [TestMethod]
    public async Task ProductionHealthExecutorPerformsAccountScopedReadAndRecordsSuccess()
    {
        var connections = new MarketplaceConnectionStore(root);
        var connection = connections.Save("etsy", "123", "Background Etsy", true);
        var credentials = new EtsyCredentials("key", "shared-secret", "", "123");
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"shop_id\":123,\"user_id\":456,\"shop_name\":\"Store\",\"currency_code\":\"TRY\"}", Encoding.UTF8, "application/json")
        });
        using var executor = new MarketplaceHealthReadExecutor(root, () => new HttpClient(handler, false), _ => credentials);

        await executor.ExecuteAsync(new AutomationJob { Kind = AutomationKind.Health, Channel = "etsy", Shop = "123" }, CancellationToken.None);

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(HttpMethod.Get, handler.LastMethod);
        Assert.AreEqual("CONNECTED_READ_ONLY", connections.Get(connection.Id)!.Status);
        Assert.AreEqual("HEALTHY", new ApiHealthStore(root).Get("etsy", "123")!.State);
    }

    [TestMethod]
    public async Task ProductionHealthFailureRecordsCurrentAccountOnlyAfterRevisionFence()
    {
        var connections = new MarketplaceConnectionStore(root);
        var connection = connections.Save("etsy", "123", "Background Etsy", true);
        var credentials = new EtsyCredentials("key", "shared-secret", "", "123");
        var handler = new RecordingHttpHandler(_ => throw new HttpRequestException("offline"));
        using var executor = new MarketplaceHealthReadExecutor(root, () => new HttpClient(handler, false), _ => credentials);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(new AutomationJob { Kind = AutomationKind.Health, Channel = "etsy", Shop = "123" }, CancellationToken.None));

        Assert.AreEqual("FAILED", connections.Get(connection.Id)!.Status);
        var observed = new ApiHealthStore(root).Get("etsy", "123");
        Assert.IsNotNull(observed);
        Assert.AreNotEqual("HEALTHY", observed.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(observed.LastError));
    }

    [TestMethod]
    public async Task HealthSuccessDoesNotWriteTelemetryAfterConnectionIsDisabledDuringRead()
    {
        var connections = new MarketplaceConnectionStore(root);
        var connection = connections.Save("etsy", "123", "Background Etsy", true);
        var credentials = new EtsyCredentials("key", "shared-secret", "", "123");
        var handler = new RecordingHttpHandler(_ =>
        {
            connections.SetEnabled(connection.Id, false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"shop_id\":123,\"user_id\":456,\"shop_name\":\"Store\",\"currency_code\":\"TRY\"}", Encoding.UTF8, "application/json")
            };
        });
        using var executor = new MarketplaceHealthReadExecutor(root, () => new HttpClient(handler, false), _ => credentials);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(new AutomationJob { Kind = AutomationKind.Health, Channel = "etsy", Shop = "123" }, CancellationToken.None));

        Assert.AreEqual("DISABLED", connections.Get(connection.Id)!.Status);
        Assert.IsNull(new ApiHealthStore(root).Get("etsy", "123"), "A stale successful probe must not create health telemetry.");
    }

    [TestMethod]
    public async Task HealthFailureDoesNotWriteTelemetryAfterConnectionIsReconfiguredDuringRead()
    {
        var connections = new MarketplaceConnectionStore(root);
        var connection = connections.Save("etsy", "123", "Background Etsy", true);
        var credentials = new EtsyCredentials("key", "shared-secret", "", "123");
        var handler = new RecordingHttpHandler(_ =>
        {
            connections.Save("etsy", "123", "Reconfigured Etsy", true, connection.Id);
            throw new HttpRequestException("stale offline result");
        });
        using var executor = new MarketplaceHealthReadExecutor(root, () => new HttpClient(handler, false), _ => credentials);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(new AutomationJob { Kind = AutomationKind.Health, Channel = "etsy", Shop = "123" }, CancellationToken.None));

        var current = connections.Get(connection.Id)!;
        Assert.AreEqual("Reconfigured Etsy", current.DisplayName);
        Assert.AreEqual("NOT_CONFIGURED", current.Status);
        Assert.IsNull(new ApiHealthStore(root).Get("etsy", "123"), "A stale failed probe must not create health telemetry.");
    }

    [TestMethod]
    public async Task FailedAutomationKeepsLastRunUnsetAndRecordsBoundedRedactedRetry()
    {
        var now = DateTime.UtcNow;
        var automation = new AutomationStore(root);
        var health = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Health, TemplateKey = "health-check", Channel = "etsy", Shop = "123",
            NextRunUtc = now.AddMinutes(-1), RetryLimit = 3, RetryBackoffMinutes = 5
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(), () => now);
        using var executor = new FailingHealthExecutor("https://probe.invalid/?token=super-secret&detail=" + new string('x', 800));
        using var job = new AutomationReadQueueBackgroundJob(root, xml, executor, () => now);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        var failed = automation.Get(health.Id);
        Assert.IsNull(failed.LastRunUtc);
        Assert.AreEqual(1, failed.FailureCount);
        Assert.IsTrue(failed.NextRunUtc > now);
        Assert.IsNull(failed.LockedUntilUtc);
        Assert.IsFalse(failed.LastError.Contains("super-secret", StringComparison.Ordinal));
        Assert.IsTrue(failed.LastError.Length <= BackgroundAppController.MaxNotificationLength);
        Assert.AreEqual(0, new SyncStore(root).List().Count);
    }

    [TestMethod]
    public async Task UnifiedDispatcherStillRunsDueAutoImportSources()
    {
        const string feed = "<Items><Item><sku>SKU-NATIVE</sku><name>Native</name><price>9</price><stock>2</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var catalog = new CatalogStore(root);
        catalog.SaveSource(XmlSourceFor(Guid.NewGuid().ToString("N"), "https://example.test/native.xml", autoImport: true));
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(feed, Encoding.UTF8, "application/xml")
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.Calls);
        Assert.IsTrue(catalog.Products().Any(product => product.Sku == "SKU-NATIVE"));
    }

    [TestMethod]
    public async Task UnifiedDispatcherDoesNotRunAutomationManagedAutoImportTwice()
    {
        const string feed = "<Items><Item><sku>SKU-ONCE</sku><name>Once</name><price>8</price><stock>1</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var catalog = new CatalogStore(root);
        var sourceId = Guid.NewGuid().ToString("N");
        catalog.SaveSource(XmlSourceFor(sourceId, "https://example.test/shared.xml", autoImport: true));
        new AutomationStore(root).Save(new AutomationJob
        {
            Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId,
            NextRunUtc = now.AddMinutes(-1)
        });
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(feed, Encoding.UTF8, "application/xml")
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.Calls, "An XML source managed by an automation lease must be excluded from the separate AutoImport due pass.");
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
    }

    [TestMethod]
    public async Task TwoDueXmlAutomationsForOneSourceExecuteOnceAndShareSuccess()
    {
        const string feed = "<Items><Item><sku>SKU-SHARED</sku><name>Shared</name><price>8</price><stock>1</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var sourceId = Guid.NewGuid().ToString("N");
        new CatalogStore(root).SaveSource(XmlSourceFor(sourceId, "https://example.test/shared.xml", autoImport: false));
        var automation = new AutomationStore(root);
        var first = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-2) });
        var second = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-1) });
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(feed, Encoding.UTF8, "application/xml")
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
        Assert.IsNotNull(automation.Get(first.Id).LastRunUtc);
        Assert.IsNotNull(automation.Get(second.Id).LastRunUtc);
    }

    [TestMethod]
    public async Task TwoDueXmlAutomationsForOneSourceExecuteOnceAndShareFailure()
    {
        var now = DateTime.UtcNow;
        var sourceId = Guid.NewGuid().ToString("N");
        new CatalogStore(root).SaveSource(XmlSourceFor(sourceId, "https://example.test/failing-shared.xml", autoImport: false));
        var automation = new AutomationStore(root);
        var first = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-2) });
        var second = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-1) });
        var handler = new RecordingHttpHandler(_ => throw new HttpRequestException("shared source unavailable"));
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
        Assert.AreEqual(1, automation.Get(first.Id).FailureCount);
        Assert.AreEqual(1, automation.Get(second.Id).FailureCount);
        Assert.AreEqual(automation.Get(first.Id).LastError, automation.Get(second.Id).LastError);
    }

    [TestMethod]
    public async Task CaseDistinctSourceIdsEachExecuteAndCompleteTheirOwnAutomation()
    {
        const string upperSourceId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        const string lowerSourceId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string upperFeed = "<Items><Item><sku>SKU-UPPER</sku><name>Upper</name><price>8</price><stock>1</stock></Item></Items>";
        const string lowerFeed = "<Items><Item><sku>SKU-LOWER</sku><name>Lower</name><price>9</price><stock>2</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var catalog = new CatalogStore(root);
        catalog.SaveSource(XmlSourceFor(upperSourceId, "https://example.test/upper.xml", autoImport: false));
        catalog.SaveSource(XmlSourceFor(lowerSourceId, "https://example.test/lower.xml", autoImport: false));
        var automation = new AutomationStore(root);
        var upper = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = upperSourceId, NextRunUtc = now.AddMinutes(-1) });
        var lower = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = lowerSourceId, NextRunUtc = now.AddMinutes(-1) });
        var handler = new RecordingHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("upper.xml", StringComparison.Ordinal) ? upperFeed : lowerFeed,
                Encoding.UTF8, "application/xml")
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(2, handler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(upperSourceId, 10).Count);
        Assert.AreEqual(1, new XmlRunStore(root).List(lowerSourceId, 10).Count);
        Assert.IsNotNull(automation.Get(upper.Id).LastRunUtc);
        Assert.IsNotNull(automation.Get(lower.Id).LastRunUtc);
        Assert.IsTrue(catalog.Products().Any(product => product.SourceId == upperSourceId && product.Sku == "SKU-UPPER"));
        Assert.IsTrue(catalog.Products().Any(product => product.SourceId == lowerSourceId && product.Sku == "SKU-LOWER"));
    }

    [TestMethod]
    public async Task ConcurrentDispatcherTicksCoalesceOneSourceExecutionAndRestartDoesNotRepeatIt()
    {
        const string feed = "<Items><Item><sku>SKU-CONCURRENT</sku><name>Concurrent</name><price>8</price><stock>1</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var sourceId = Guid.NewGuid().ToString("N");
        new CatalogStore(root).SaveSource(XmlSourceFor(sourceId, "https://example.test/concurrent.xml", autoImport: false));
        var automation = new AutomationStore(root);
        var first = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-2) });
        var second = automation.Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-1) });
        var handler = new PausingHttpHandler(feed);
        using (var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now))
        using (var health = new RecordingHealthExecutor())
        using (var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now))
        {
            var firstTick = job.ExecuteAsync(CancellationToken.None);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var concurrentTick = job.ExecuteAsync(CancellationToken.None);
            handler.Release.TrySetResult();
            await Task.WhenAll(firstTick, concurrentTick);
        }

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
        Assert.IsNotNull(automation.Get(first.Id).LastRunUtc);
        Assert.IsNotNull(automation.Get(second.Id).LastRunUtc);

        var restartHandler = new RecordingHttpHandler(_ => throw new InvalidOperationException("Completed jobs must not repeat after restart."));
        using var restartXml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(restartHandler, false), () => now.AddSeconds(1));
        using var restartHealth = new RecordingHealthExecutor();
        using var restartedJob = new AutomationReadQueueBackgroundJob(root, restartXml, restartHealth, () => now.AddSeconds(1));
        await restartedJob.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(0, restartHandler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
    }

    [TestMethod]
    public async Task ConcurrentAutoImportAndAutomationShareOneSourceExecution()
    {
        const string feed = "<Items><Item><sku>SKU-AUTO-SHARED</sku><name>Auto shared</name><price>8</price><stock>1</stock></Item></Items>";
        var now = DateTime.UtcNow;
        var sourceId = Guid.NewGuid().ToString("N");
        new CatalogStore(root).SaveSource(XmlSourceFor(sourceId, "https://example.test/auto-shared.xml", autoImport: true));
        var automation = new AutomationStore(root);
        var automationJob = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId,
            NextRunUtc = now.AddMinutes(-1)
        });
        var handler = new PausingHttpHandler(feed);
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var dispatcher = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);

        var autoImport = xml.ExecuteAsync(CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var automationRun = dispatcher.ExecuteAsync(CancellationToken.None);
        handler.Release.TrySetResult();
        await Task.WhenAll(autoImport, automationRun);

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(1, new XmlRunStore(root).List(sourceId, 10).Count);
        Assert.IsNotNull(automation.Get(automationJob.Id).LastRunUtc);
    }

    [TestMethod]
    public async Task SameSourceIdInIndependentDataDirectoriesDoesNotCoalesceAcrossProfiles()
    {
        const string feedA = "<Items><Item><sku>SKU-PROFILE-A</sku><name>A</name><price>8</price><stock>1</stock></Item></Items>";
        const string feedB = "<Items><Item><sku>SKU-PROFILE-B</sku><name>B</name><price>9</price><stock>2</stock></Item></Items>";
        var sourceId = Guid.NewGuid().ToString("N");
        var rootB = root + "-other";
        try
        {
            var now = DateTime.UtcNow;
            new CatalogStore(root).SaveSource(XmlSourceFor(sourceId, "https://example.test/a.xml", autoImport: false));
            new CatalogStore(rootB).SaveSource(XmlSourceFor(sourceId, "https://example.test/b.xml", autoImport: false));
            new AutomationStore(root).Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-1) });
            new AutomationStore(rootB).Save(new AutomationJob { Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId, NextRunUtc = now.AddMinutes(-1) });
            var handlerA = new PausingHttpHandler(feedA);
            var handlerB = new PausingHttpHandler(feedB);
            using var xmlA = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handlerA, false), () => now);
            using var xmlB = new ScheduledXmlImportBackgroundJob(rootB, new HttpClient(handlerB, false), () => now);
            using var healthA = new RecordingHealthExecutor();
            using var healthB = new RecordingHealthExecutor();
            using var dispatcherA = new AutomationReadQueueBackgroundJob(root, xmlA, healthA, () => now);
            using var dispatcherB = new AutomationReadQueueBackgroundJob(rootB, xmlB, healthB, () => now);

            var runA = dispatcherA.ExecuteAsync(CancellationToken.None);
            await handlerA.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var runB = dispatcherB.ExecuteAsync(CancellationToken.None);
            Assert.AreEqual(1, handlerB.Calls, "A second profile must start its own source execution immediately.");
            handlerA.Release.TrySetResult();
            handlerB.Release.TrySetResult();
            await Task.WhenAll(runA, runB);

            Assert.AreEqual(1, handlerA.Calls);
            Assert.AreEqual(1, handlerB.Calls);
            Assert.IsTrue(new CatalogStore(root).Products().Any(product => product.Sku == "SKU-PROFILE-A"));
            Assert.IsTrue(new CatalogStore(rootB).Products().Any(product => product.Sku == "SKU-PROFILE-B"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
        }
    }

    [TestMethod]
    public async Task DifferentXmlSourcesRunOneAfterAnotherInTheSharedQueue()
    {
        const string feed = "<Items><Item><sku>SKU-QUEUE</sku><name>Queued</name><price>8</price><stock>1</stock></Item></Items>";
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var catalog = new CatalogStore(root);
        catalog.SaveSource(XmlSourceFor(firstId, "https://example.test/first.xml", autoImport: false));
        catalog.SaveSource(XmlSourceFor(secondId, "https://example.test/second.xml", autoImport: false));
        var handler = new ConcurrencyHttpHandler(feed);
        using var job = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false));

        await Task.WhenAll(job.ExecuteSourceAsync(firstId, CancellationToken.None), job.ExecuteSourceAsync(secondId, CancellationToken.None));

        Assert.AreEqual(1, handler.MaximumConcurrentCalls, "Bir XML tamamlanmadan sonraki XML başlamamalı.");
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task CancelledAutomationReleasesItsLeaseWithoutAdvancingOrFailingSchedule()
    {
        var now = DateTime.UtcNow;
        var automation = new AutomationStore(root);
        var health = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Health, TemplateKey = "health-check", Channel = "etsy", Shop = "123",
            NextRunUtc = now.AddMinutes(-1)
        });
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(), () => now);
        using var executor = new BlockingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, executor, () => now);
        using var cancellation = new CancellationTokenSource();

        var run = job.ExecuteAsync(cancellation.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(automation.Get(health.Id).LockedUntilUtc);
        cancellation.Cancel();

        try { await run; Assert.Fail("Cancellation must propagate to the controller lifetime."); }
        catch (OperationCanceledException) { }
        var cancelled = automation.Get(health.Id);
        Assert.IsNull(cancelled.LockedUntilUtc);
        Assert.IsNull(cancelled.LastRunUtc);
        Assert.AreEqual(0, cancelled.FailureCount);
        Assert.IsTrue(cancelled.NextRunUtc <= now);
    }

    [TestMethod]
    public async Task CancelledXmlAutomationDoesNotAdvanceSourceOrAutomationSchedule()
    {
        var now = DateTime.UtcNow;
        var sourceId = Guid.NewGuid().ToString("N");
        var catalog = new CatalogStore(root);
        catalog.SaveSource(XmlSourceFor(sourceId, "https://example.test/blocking.xml", autoImport: false));
        var automation = new AutomationStore(root);
        var automationJob = automation.Save(new AutomationJob
        {
            Kind = AutomationKind.Xml, TemplateKey = "xml-refresh", Channel = "local", Shop = sourceId,
            NextRunUtc = now.AddMinutes(-1)
        });
        var handler = new BlockingHttpHandler();
        using var xml = new ScheduledXmlImportBackgroundJob(root, new HttpClient(handler, false), () => now);
        using var health = new RecordingHealthExecutor();
        using var job = new AutomationReadQueueBackgroundJob(root, xml, health, () => now);
        using var cancellation = new CancellationTokenSource();

        var run = job.ExecuteAsync(cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try { await run; Assert.Fail("Cancellation must propagate to the controller lifetime."); }
        catch (OperationCanceledException) { }
        Assert.IsNull(catalog.Sources().Single(source => source.Id == sourceId).LastRunUtc);
        Assert.IsNull(automation.Get(automationJob.Id).LastRunUtc);
        Assert.IsNull(automation.Get(automationJob.Id).LockedUntilUtc);
    }

    [TestMethod]
    public void ProductionFactoryContainsOnlyLocalOrRemoteReadJobs()
    {
        using var jobs = new DisposableJobs(BackgroundJobFactory.Create(root));
        Assert.IsTrue(jobs.Items.Count >= 2);
        Assert.IsTrue(jobs.Items.All(job => job.Access is BackgroundJobAccess.LocalOnly or BackgroundJobAccess.RemoteReadOnly));
        Assert.IsFalse(jobs.Items.Any(job => job is ScheduledXmlImportBackgroundJob), "XML source intervals and XML automation leases must share the same dispatcher, not independent top-level loops.");
    }

    [TestMethod]
    public void UnifiedAutomationDispatcherDeclaresItsRemoteReadBoundary()
    {
        using var job = new AutomationReadQueueBackgroundJob(root);
        Assert.AreEqual(BackgroundJobAccess.RemoteReadOnly, job.Access);
    }

    static XmlSource XmlSourceFor(string id, string location, bool autoImport) => new()
    {
        Id = id, Name = id, Location = location, Enabled = true, AutoImport = autoImport, IntervalMinutes = 1,
        ItemPath = "/Items/Item", Currency = "TRY", MaximumStock = 100,
        Fields = new() { ["Sku"] = "sku", ["Name"] = "name", ["Cost"] = "price", ["Stock"] = "stock" }
    };

    sealed class DisposableJobs(IReadOnlyList<IBackgroundJob> items) : IDisposable
    {
        public IReadOnlyList<IBackgroundJob> Items { get; } = items;
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }

    sealed class RecordingHealthExecutor : IMarketplaceHealthReadExecutor
    {
        public int Calls { get; private set; }
        public string LastJobId { get; private set; } = "";
        public Task ExecuteAsync(AutomationJob job, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            LastJobId = job.Id;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    sealed class FailingHealthExecutor(string message) : IMarketplaceHealthReadExecutor
    {
        public Task ExecuteAsync(AutomationJob job, CancellationToken token) => throw new InvalidOperationException(message);
        public void Dispose() { }
    }

    sealed class BlockingHealthExecutor : IMarketplaceHealthReadExecutor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(AutomationJob job, CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public void Dispose() { }
    }

    sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod LastMethod { get; private set; } = HttpMethod.Get;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastMethod = request.Method;
            return Task.FromResult(respond(request));
        }
    }

    sealed class BlockingHttpHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    sealed class PausingHttpHandler(string xml) : HttpMessageHandler
    {
        int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        }
    }

    sealed class ConcurrencyHttpHandler(string xml) : HttpMessageHandler
    {
        int calls;
        int active;
        int maximumConcurrentCalls;
        public int Calls => Volatile.Read(ref calls);
        public int MaximumConcurrentCalls => Volatile.Read(ref maximumConcurrentCalls);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maximumConcurrentCalls)))
                if (Interlocked.CompareExchange(ref maximumConcurrentCalls, current, observed) == observed) break;
            try
            {
                await Task.Delay(150, cancellationToken);
                var sourceXml = xml.Replace("SKU-QUEUE", request.RequestUri!.AbsolutePath.Contains("first", StringComparison.Ordinal) ? "SKU-FIRST" : "SKU-SECOND");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sourceXml, Encoding.UTF8, "application/xml") };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
