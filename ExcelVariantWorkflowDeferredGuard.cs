namespace TrMarketplaceHubDesktop;

public sealed record ExcelVariantWorkflowDecision(string Status, string Detail, string Mode);

public static class ExcelVariantWorkflowDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";

    public static ExcelVariantWorkflowDecision Evaluate(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) throw new ArgumentException("Excel varyant modu zorunlu.", nameof(mode));
        return new(Status, "Excel varyant import/export ve toplu güncelleme kullanıcı tarafından ertelendi; workbook apply başlatılmadı.", mode.Trim());
    }

    public static void EnsureNoApply(ExcelVariantWorkflowDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: Excel varyant apply kapsam dışı; kalıcı değişiklik başlatılmadı.");
    }
}
