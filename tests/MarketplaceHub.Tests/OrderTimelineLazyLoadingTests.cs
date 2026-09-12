using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #782 (PERFORMANCE: Order timeline lazy loading). ENTRY: order detail timeline (OrdersPanel.ShipmentEditor),
// which used to create one TextBlock per event for the whole history on the UI thread in one go. The loader
// owns the cursor and hands out the timeline newest-first in pages, appending each page in chunks with a
// yield between chunks so the dispatcher can process input; the UI only supplies the sink and the yield.
[TestClass]
public sealed class OrderTimelineLazyLoadingTests
{
    static List<OrderTrackingEvent> Events(int count)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Deliberately unsorted input: the loader must order newest-first itself, exactly like the old code did.
        return Enumerable.Range(0, count).Select(i => new OrderTrackingEvent(start.AddMinutes(i), i % 2 == 0 ? "InTransit" : "Shipped", "Yerel / manuel")).OrderBy(_ => Guid.NewGuid()).ToList();
    }

    static Task NoYield() => Task.CompletedTask;

    [TestMethod]
    public async Task AThousandEventsArePagedNewestFirstWithoutLossOrDuplication()
    {
        var loader = new OrderTimelineLoader(Events(1000), pageSize: 50, chunkSize: 25);
        var rendered = new List<OrderTrackingEvent>();
        var pages = 0; var yields = 0;

        while (loader.HasMore)
        {
            var appended = await loader.LoadPageAsync(loader.Cursor, rendered.Add, () => { yields++; return Task.CompletedTask; }, CancellationToken.None);
            Assert.AreEqual(50, appended, "Every page of a 1000-event history is a full page.");
            pages++;
        }

        Assert.AreEqual(1000, loader.Total);
        Assert.AreEqual(20, pages);
        Assert.AreEqual(1000, rendered.Count);
        Assert.AreEqual(1000, rendered.Distinct().Count(), "No event may be appended twice.");
        CollectionAssert.AreEqual(rendered.OrderByDescending(e => e.At).ToList(), rendered, "The timeline is rendered newest-first across page boundaries.");
        Assert.AreEqual(20, yields, "One yield between the two chunks of every page: the UI thread gets a breath inside each page, never a 50-item block.");
        Assert.AreEqual(0, await loader.LoadPageAsync(loader.Cursor, rendered.Add, NoYield, CancellationToken.None), "Nothing is left once the whole history is loaded.");
    }

    [TestMethod]
    public async Task ADuplicateRequestForAPageAlreadyInFlightOrAlreadyLoadedAppendsNothing()
    {
        var loader = new OrderTimelineLoader(Events(120), pageSize: 50, chunkSize: 25);
        var rendered = new List<OrderTrackingEvent>();
        var gate = new TaskCompletionSource();

        // First request parks at the yield between its two chunks -- a page is in flight.
        var first = loader.LoadPageAsync(0, rendered.Add, () => gate.Task, CancellationToken.None);
        Assert.IsTrue(loader.IsLoading);
        var duplicateWhileInFlight = await loader.LoadPageAsync(0, rendered.Add, NoYield, CancellationToken.None);
        gate.SetResult();
        var firstAppended = await first;

        Assert.AreEqual(0, duplicateWhileInFlight, "A second click while the page is still loading must not append the same page again.");
        Assert.AreEqual(50, firstAppended);
        Assert.AreEqual(0, await loader.LoadPageAsync(0, rendered.Add, NoYield, CancellationToken.None), "A stale cursor (page already loaded) must not append that page a second time.");
        Assert.AreEqual(0, await loader.LoadPageAsync(7, rendered.Add, NoYield, CancellationToken.None), "A cursor that is not the current position is rejected rather than slicing from an arbitrary offset.");
        Assert.AreEqual(50, await loader.LoadPageAsync(50, rendered.Add, NoYield, CancellationToken.None));
        Assert.AreEqual(20, await loader.LoadPageAsync(100, rendered.Add, NoYield, CancellationToken.None), "The last page is the remainder.");
        Assert.AreEqual(120, rendered.Count);
        Assert.AreEqual(120, rendered.Distinct().Count());
        Assert.IsFalse(loader.HasMore);
    }

    [TestMethod]
    public async Task RefreshStartsAgainFromTheNewestEventWithTheSameFirstPage()
    {
        var loader = new OrderTimelineLoader(Events(75), pageSize: 50, chunkSize: 25);
        var before = new List<OrderTrackingEvent>(); var after = new List<OrderTrackingEvent>();
        await loader.LoadPageAsync(0, before.Add, NoYield, CancellationToken.None);
        await loader.LoadPageAsync(50, before.Add, NoYield, CancellationToken.None);
        Assert.IsFalse(loader.HasMore);

        loader.Reset();

        Assert.AreEqual(0, loader.Cursor);
        Assert.IsTrue(loader.HasMore);
        Assert.AreEqual(75, loader.Remaining);
        await loader.LoadPageAsync(0, after.Add, NoYield, CancellationToken.None);
        CollectionAssert.AreEqual(before.Take(50).ToList(), after, "After a refresh the first page is the same newest-first page as before.");
    }

    [TestMethod]
    public async Task CancellationStopsAtAChunkBoundaryAndTheCursorOnlyCountsWhatWasActuallyAppended()
    {
        var loader = new OrderTimelineLoader(Events(200), pageSize: 50, chunkSize: 10);
        var rendered = new List<OrderTrackingEvent>();
        using var cts = new CancellationTokenSource();

        // The order is switched away (cancel) while the page is between its first and second chunk.
        var appended = await loader.LoadPageAsync(0, rendered.Add, () => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);

        Assert.AreEqual(10, appended, "Only the chunk that was already appended counts; nothing is appended after cancellation.");
        Assert.AreEqual(10, rendered.Count);
        Assert.AreEqual(10, loader.Cursor, "The cursor reflects what is on screen, so a later resume neither skips nor repeats events.");
        Assert.IsFalse(loader.IsLoading);

        // Resuming with a fresh token continues exactly where the screen stopped.
        Assert.AreEqual(50, await loader.LoadPageAsync(10, rendered.Add, NoYield, CancellationToken.None));
        Assert.AreEqual(60, rendered.Count);
        Assert.AreEqual(60, rendered.Distinct().Count());
        CollectionAssert.AreEqual(rendered.OrderByDescending(e => e.At).ToList(), rendered);
    }

    [TestMethod]
    public void ScrollingNearTheBottomOfTheDetailPaneRequestsTheNextPageAndScrollingElsewhereDoesNot()
    {
        Assert.IsTrue(OrderTimelineLoader.ShouldLoadOnScroll(verticalOffset: 950, viewportHeight: 400, extentHeight: 1400));
        Assert.IsTrue(OrderTimelineLoader.ShouldLoadOnScroll(verticalOffset: 1000, viewportHeight: 400, extentHeight: 1400), "Exactly at the bottom.");
        Assert.IsFalse(OrderTimelineLoader.ShouldLoadOnScroll(verticalOffset: 100, viewportHeight: 400, extentHeight: 1400), "Near the top: nothing to do.");
        Assert.IsFalse(OrderTimelineLoader.ShouldLoadOnScroll(verticalOffset: 0, viewportHeight: 0, extentHeight: 0), "A pane that has not been measured yet must not trigger a load.");
        Assert.IsTrue(OrderTimelineLoader.ShouldLoadOnScroll(verticalOffset: 0, viewportHeight: 400, extentHeight: 300), "Content shorter than the viewport: the bottom is already visible, so the next page should come in without any scrolling.");
    }

    [TestMethod]
    public void AnEmptyHistoryHasNoPagesAndNeverReportsMore()
    {
        var loader = new OrderTimelineLoader(Array.Empty<OrderTrackingEvent>());
        Assert.AreEqual(0, loader.Total);
        Assert.IsFalse(loader.HasMore);
        Assert.AreEqual(0, loader.Remaining);
    }
}
