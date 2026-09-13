using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>What the preview screen knows at this moment: the grid, the selection's findings and the flow state.</summary>
public sealed record PreviewToolbarState(
    int Rows, int Shown, int Selected, int SelectedNew, int SelectedChanged, int SelectedUnchanged, int SelectedBlocking, int SelectedWarning,
    bool XmlLoaded, bool PreviewExists, bool PreviewFresh, bool ApplyRunning, string StaleReason = "");

public sealed record PreviewToolbarModel(
    string CountsText, string ValidationText, SeverityLevel ValidationLevel,
    bool CanApply, string ApplyLabel, string ApplyReason, bool CanCancel, bool CanRecompute,
    string StatusText, SeverityLevel StatusLevel);

/// <summary>
/// The XML preview's sticky action toolbar (#831): affected counts, the selection's validation state, the preview's
/// freshness and the apply / cancel / recompute actions, composed from the page's real state so they read the same
/// wherever the grid is scrolled to. Apply is enabled only for a fresh preview with a selection that carries no
/// blocking finding while nothing runs; the reason it is disabled is spelled out. The toolbar never bypasses the
/// apply's own revision check -- it only mirrors it -- and the apply stays a local write.
/// </summary>
public static class PreviewActionToolbar
{
    public static PreviewToolbarModel Compose(PreviewToolbarState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var affected = s.SelectedNew + s.SelectedChanged;
        var counts = $"{N(s.Selected)} / {N(s.Rows)} seçili · etkilenen {N(affected)} ({N(s.SelectedNew)} yeni, {N(s.SelectedChanged)} değişen) · {N(s.SelectedUnchanged)} aynı" + (s.Shown < s.Rows ? $" · filtre: {N(s.Shown)} görünür" : "");
        var (validation, validationLevel) = s.Selected == 0
            ? ("seçim yok", SeverityLevel.Info)
            : ($"{N(s.SelectedBlocking)} engel · {N(s.SelectedWarning)} uyarı (seçili)", s.SelectedBlocking > 0 ? SeverityLevel.Blocking : s.SelectedWarning > 0 ? SeverityLevel.Warning : SeverityLevel.Success);
        var (status, statusLevel) = s.ApplyRunning ? ("Aktarım sürüyor · iptal edilebilir", SeverityLevel.Info)
            : !s.XmlLoaded ? ("XML okunmadı", SeverityLevel.Info)
            : !s.PreviewExists ? ("Önizleme hesaplanmadı", SeverityLevel.Info)
            : !s.PreviewFresh ? ("Önizleme geçersiz: " + (s.StaleReason.Length > 0 ? s.StaleReason : "XML veya eşleme değişti."), SeverityLevel.Warning)
            : ("Önizleme güncel", SeverityLevel.Success);
        var reason = s.ApplyRunning ? "Aktarım sürüyor; bitmesini bekleyin veya iptal edin."
            : !s.XmlLoaded ? "Önce XML'i okuyun."
            : !s.PreviewExists ? "Önce önizleme hesaplayın."
            : !s.PreviewFresh ? "Önizleme geçersiz; yeniden hesaplayın."
            : s.Selected == 0 ? "Önizlemeden en az bir satır seçin."
            : s.SelectedBlocking > 0 ? $"{N(s.SelectedBlocking)} seçili satırda engel var; düzeltin veya seçimden çıkarın."
            : "";
        return new(counts, validation, validationLevel, reason.Length == 0, $"Seçilileri havuza al ({N(s.Selected)}) · yerel", reason, s.ApplyRunning, s.XmlLoaded && !s.ApplyRunning, status, statusLevel);
    }

    static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
