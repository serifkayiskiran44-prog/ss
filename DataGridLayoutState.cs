using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record DataGridColumnLayout(string Key, int DisplayIndex, double Width, bool Visible);

public sealed record DataGridLayoutState(int Version, IReadOnlyList<DataGridColumnLayout> Columns, string SortBy, bool SortDescending);

/// <summary>
/// Persistence format and fallback rules for a DataGrid's column layout (#792). Keys are binding paths, not
/// headers, so a renamed or translated header keeps its layout; widths are device-independent pixels, so a
/// layout saved at 100% DPI lays out the same at 150% and 200%. Anything that cannot be trusted -- empty, not
/// JSON, another schema version, no columns -- reads back as null and the caller keeps the default layout.
/// </summary>
public static class DataGridLayoutCodec
{
    public const int CurrentVersion = 1;

    public static string Serialize(DataGridLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(state with { Version = CurrentVersion, SortBy = state.SortBy ?? "" });
    }

    public static DataGridLayoutState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<DataGridLayoutState>(json);
            if (state is null || state.Version != CurrentVersion || state.Columns is null) return null;
            var columns = state.Columns.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Key) && double.IsFinite(c.Width) && c.Width >= 0).ToArray();
            return state with { Columns = columns, SortBy = state.SortBy ?? "" };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Final left-to-right order of the live columns: the persisted ones by their saved index (a saved key that
    /// no longer exists is skipped; a collision keeps the default relative order), then every column the saved
    /// layout does not know, inserted at its default position -- a column added by a newer version shows up
    /// where the code puts it, not at the far end.
    /// </summary>
    public static IReadOnlyList<string> ResolveOrder(IReadOnlyList<string> liveKeys, IReadOnlyList<DataGridColumnLayout> persisted)
    {
        ArgumentNullException.ThrowIfNull(liveKeys); ArgumentNullException.ThrowIfNull(persisted);
        var live = liveKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        var ordered = persisted.Where(p => live.Contains(p.Key, StringComparer.Ordinal)).OrderBy(p => p.DisplayIndex).ThenBy(p => live.IndexOf(p.Key)).Select(p => p.Key).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < live.Count; i++) if (!ordered.Contains(live[i], StringComparer.Ordinal)) ordered.Insert(Math.Min(i, ordered.Count), live[i]);
        return ordered;
    }
}
