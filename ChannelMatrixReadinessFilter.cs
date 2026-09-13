namespace TrMarketplaceHubDesktop;

public sealed record ChannelMatrixReadinessResult(ChannelMatrix Matrix, IReadOnlyList<(ChannelMatrixLegendEntry Entry, int Count)> Counts, IReadOnlySet<string> Active, int HiddenRows)
{
    public bool IsEmpty => Matrix.Rows.Count == 0;
    /// <summary>What to say when nothing is left: which filters hid everything.</summary>
    public string EmptyText => Active.Count == 0 ? "Gösterilecek ürün yok." : $"Seçili durum filtreleriyle eşleşen hücre yok ({string.Join(", ", Active.Select(k => ChannelMatrixLegend.Get(k).Word))}); filtreyi kaldırın.";
}

/// <summary>
/// Readiness filters over the matrix (#844): the legend's states double as filters. A row stays when any of its
/// cells is in one of the active states (filters combine as "any of"); no active state means every row. Counts
/// always come from the unfiltered matrix, so a chip says what exists even while another chip hides it. Only
/// legend keys are honoured -- a key the legend does not know is dropped, never invented into a state. The pass
/// is one walk over the cells, so a 100 000-cell matrix filters in milliseconds.
/// </summary>
public static class ChannelMatrixReadinessFilter
{
    public static ChannelMatrixReadinessResult Apply(ChannelMatrix matrix, IEnumerable<string>? activeStates)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var known = new HashSet<string>(ChannelMatrixLegend.Entries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>((activeStates ?? Array.Empty<string>()).Where(k => known.Contains(k)).Select(k => k.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        var counts = ChannelMatrixLegend.Counts(matrix);
        if (active.Count == 0) return new(matrix, counts, active, 0);
        var rows = matrix.Rows.Where(r => r.Cells.Any(c => active.Contains((c ?? ChannelMatrixRow.Empty).Legend.Key))).ToList();
        return new(new ChannelMatrix(matrix.Columns, rows), counts, active, matrix.Rows.Count - rows.Count);
    }

    /// <summary>Toggle one state in a filter set; the result is a new set, the input untouched.</summary>
    public static IReadOnlySet<string> Toggle(IReadOnlySet<string> active, string key)
    {
        ArgumentNullException.ThrowIfNull(active);
        var next = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);
        if (!next.Remove(key)) next.Add(key.ToUpperInvariant());
        return next;
    }
}
