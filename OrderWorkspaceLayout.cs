using System.Globalization;

namespace TrMarketplaceHubDesktop;

public enum OrderWorkspaceMode { SideBySide, Stacked }

/// <summary>
/// The order workspace's split between list and detail (#836): a detail width in device-independent pixels
/// remembered across restarts and clamped so neither pane can vanish (the list keeps at least 480 DIP, the
/// detail at least 280 and at most 45 % of the workspace), a stacked fallback below 900 DIP where the detail
/// sits under the list, a selection key that survives a reload (the store hands out new instances), and the rule
/// that a detail never opens for an order outside the store the list is filtered to.
/// </summary>
public static class OrderWorkspaceLayout
{
    public const string SplitPreferenceKey = "layout:orders:split";
    public const double DefaultDetailWidth = 370;
    public const double MinListWidth = 480;
    public const double MinDetailWidth = 280;
    public const double MaxDetailShare = 0.45;
    public const double StackBelowWidth = 900;
    public const double StackedDetailShare = 0.45;

    public static OrderWorkspaceMode Mode(double workspaceWidth) => workspaceWidth > 0 && workspaceWidth < StackBelowWidth ? OrderWorkspaceMode.Stacked : OrderWorkspaceMode.SideBySide;

    /// <summary>The detail width that fits: never below the minimum, never so wide the list drops under its minimum, never more than its share.</summary>
    public static double ClampDetailWidth(double requested, double workspaceWidth)
    {
        var width = double.IsFinite(requested) && requested > 0 ? requested : DefaultDetailWidth;
        if (!double.IsFinite(workspaceWidth) || workspaceWidth <= 0) return Math.Max(MinDetailWidth, width);
        var max = Math.Max(MinDetailWidth, Math.Min(workspaceWidth * MaxDetailShare, workspaceWidth - MinListWidth));
        return Math.Clamp(width, MinDetailWidth, max);
    }

    public static string Serialize(double detailWidth) => detailWidth.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A remembered width; anything untrusted (empty, not a number, absurd) reads as the default.</summary>
    public static double Deserialize(string? value) => TryDeserialize(value, out var width) ? width : DefaultDetailWidth;

    /// <summary>The Try form of <see cref="Deserialize"/>: a finite number inside the usable band, nothing else.</summary>
    public static bool TryDeserialize(string? value, out double width)
    {
        if (double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out width) && double.IsFinite(width) && width >= MinDetailWidth && width <= 4000) return true;
        width = DefaultDetailWidth; return false;
    }

    public static string SelectionKey(OrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return $"{order.Marketplace}{order.ShopId}{order.OrderId}";
    }

    /// <summary>The same order among fresh instances, or null when it is gone or no longer listed.</summary>
    public static OrderSnapshot? Restore(IEnumerable<OrderSnapshot> listed, string? selectionKey)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return string.IsNullOrEmpty(selectionKey) ? null : listed.FirstOrDefault(o => SelectionKey(o) == selectionKey);
    }

    /// <summary>A detail belongs to the store the list shows: with a marketplace or shop filter set, an order from elsewhere never opens.</summary>
    public static bool CanOpenDetail(OrderSnapshot order, string? marketplaceFilter, string? shopFilter, string all = "Tümü")
    {
        ArgumentNullException.ThrowIfNull(order);
        var marketplaceOk = string.IsNullOrEmpty(marketplaceFilter) || marketplaceFilter == all || string.Equals(order.Marketplace, marketplaceFilter, StringComparison.OrdinalIgnoreCase);
        var shopOk = string.IsNullOrEmpty(shopFilter) || shopFilter == all || string.Equals(order.ShopId, shopFilter, StringComparison.Ordinal);
        return marketplaceOk && shopOk;
    }
}
