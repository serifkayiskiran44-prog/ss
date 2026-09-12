using System.Globalization;
using System.Windows.Input;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Keyboard movement and the selected-item summary for the product media gallery (#804). The movement rules
/// live here rather than inside a key handler so the edges are decided once and can be tested: the strip stops
/// at its ends instead of wrapping (wrapping surprises someone scanning left to right), an index left over from
/// a longer gallery is clamped rather than trusted, and an empty gallery stays "nothing selected".
/// The summary is host-only, reusing the shared media wording and URL rule from #797, because it is the line an
/// operator screenshots and a supplier image URL carries a signature in its query string.
/// </summary>
public static class MediaGalleryNavigation
{
    /// <summary>The index the gallery should move to, or the current one for a key it does not own.</summary>
    public static int Next(int currentIndex, int count, Key key)
    {
        if (count <= 0) return -1;
        var current = currentIndex < 0 || currentIndex >= count ? -1 : currentIndex;
        return key switch
        {
            Key.Right or Key.Down => current < 0 ? 0 : Math.Min(current + 1, count - 1),
            Key.Left or Key.Up => current < 0 ? 0 : Math.Max(current - 1, 0),
            Key.Home => 0,
            Key.End => count - 1,
            _ => current < 0 ? currentIndex : current,
        };
    }

    public static bool IsActivation(Key key) => key is Key.Enter or Key.Space;

    public static string Summary(IReadOnlyList<ProductMediaRecord> records, int index)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0 || index < 0 || index >= records.Count) return "Seçili görsel yok";
        var record = records[index];
        var state = ProductMediaPresentation.Classify(record, loading: false, loaded: record.Status == MediaStatus.Ready, fromCache: false);
        var parts = new List<string>
        {
            $"{(index + 1).ToString(CultureInfo.CurrentCulture)} / {records.Count.ToString(CultureInfo.CurrentCulture)}",
            state.Label,
            "kaynak: " + ProductMediaPresentation.SafeSourceLabel(record.Url),
        };
        if (record.IsPrimary) parts.Add("Ana görsel");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The value an *explicit* "copy this image's address" action puts on the clipboard. Deliberate copying of a
    /// single URL the operator asked for is legitimate; what must not happen is a bulk or implicit copy sweeping
    /// signed URLs out of a grid, which is why the gallery disables implicit clipboard copy and routes anything
    /// automatic through <see cref="ClipboardRedaction"/> instead.
    /// </summary>
    public static string ExplicitCopyValue(ProductMediaRecord? record) => record?.Url ?? "";
}
