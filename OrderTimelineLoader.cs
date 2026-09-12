using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Pages a shipment's observation timeline newest-first for the order detail pane (#782). The events are
/// already in memory (they travel inside the order's JSON payload); the cost is creating and laying out one
/// element per event on the UI thread, which for a long history used to happen in a single block. The loader
/// owns the cursor and appends one page at a time in chunks, calling <c>yield</c> between chunks so the
/// dispatcher can service input; the UI supplies only the sink and the yield. The cursor advances per item
/// actually appended, so a cancelled page leaves the cursor exactly at what is on screen and a later resume
/// neither skips nor repeats events. A request while a page is in flight, or for a cursor that is not the
/// current position (a double click, a late scroll event), appends nothing -- the same page can never be
/// appended twice.
/// </summary>
public sealed class OrderTimelineLoader
{
    public const int DefaultPageSize = 50;
    public const int DefaultChunkSize = 25;
    public const double DefaultScrollThreshold = 64;

    readonly IReadOnlyList<OrderTrackingEvent> ordered;
    readonly int pageSize;
    readonly int chunkSize;

    public OrderTimelineLoader(IEnumerable<OrderTrackingEvent> events, int pageSize = DefaultPageSize, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (chunkSize < 1) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        ordered = events.OrderByDescending(e => e.At).ToArray();
        this.pageSize = pageSize;
        this.chunkSize = Math.Min(chunkSize, pageSize);
    }

    public int Total => ordered.Count;
    public int Cursor { get; private set; }
    public bool HasMore => Cursor < Total;
    public int Remaining => Total - Cursor;
    public bool IsLoading { get; private set; }

    /// <summary>Refresh: start again from the newest event.</summary>
    public void Reset()
    {
        if (IsLoading) throw new InvalidOperationException("Gözlem geçmişi bir sayfa yüklenirken sıfırlanamaz.");
        Cursor = 0;
    }

    /// <summary>
    /// Appends the next page for <paramref name="cursor"/> through <paramref name="append"/>, chunk by chunk,
    /// awaiting <paramref name="yield"/> between chunks. Returns the number of events appended: 0 when a page
    /// is already in flight, when the cursor is not the current position, when nothing is left, or when the
    /// token was already cancelled. Cancellation is honoured at chunk boundaries.
    /// </summary>
    public async Task<int> LoadPageAsync(int cursor, Action<OrderTrackingEvent> append, Func<Task> yield, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        ArgumentNullException.ThrowIfNull(yield);
        if (IsLoading || cursor != Cursor || !HasMore || cancellationToken.IsCancellationRequested) return 0;
        IsLoading = true;
        try
        {
            var end = Math.Min(Cursor + pageSize, Total);
            var appended = 0;
            while (Cursor < end)
            {
                var chunkEnd = Math.Min(Cursor + chunkSize, end);
                while (Cursor < chunkEnd) { append(ordered[Cursor]); Cursor++; appended++; }
                if (Cursor >= end) break;
                await yield();
                if (cancellationToken.IsCancellationRequested) break;
            }
            return appended;
        }
        finally { IsLoading = false; }
    }

    /// <summary>True when the bottom of the scrollable detail pane is within <paramref name="threshold"/> pixels of view (or the content is shorter than the viewport), i.e. the next page should be requested. A pane that has not been measured yet (zero viewport) never triggers.</summary>
    public static bool ShouldLoadOnScroll(double verticalOffset, double viewportHeight, double extentHeight, double threshold = DefaultScrollThreshold)
        => viewportHeight > 0 && verticalOffset + viewportHeight >= extentHeight - threshold;
}
