namespace TrMarketplaceHubDesktop;

public sealed record InlineBulkEditDecision(string Status, string Detail, string Field);

public static class InlineBulkEditDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static InlineBulkEditDecision Evaluate(string field)
    {
        if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("Alan adı zorunlu.", nameof(field));
        return new(Status, "Hızlı satır içi düzenleme kullanıcı tarafından ertelendi; grid üzerinde kalıcı local edit başlatılmadı.", field.Trim());
    }
    public static void EnsureNoEdit(InlineBulkEditDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: inline bulk edit kapsam dışı; edit başlatılmadı.");
    }
}
