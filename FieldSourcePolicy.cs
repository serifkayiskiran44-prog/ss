namespace TrMarketplaceHubDesktop;

public sealed record FieldSourceRule(string ProductId, string Field, string Owner, bool Locked, long Version);
public sealed record FieldApplyDecision(string ProductId, string Field, string Source, string Decision, string Reason, long Version);

public sealed class FieldSourcePolicy
{
    private static readonly string[] Allowed = { "manual", "xml", "excel", "channel", "import" };
    private readonly object gate = new();
    private readonly Dictionary<(string Product, string Field), FieldSourceRule> rules = new();
    private readonly List<FieldApplyDecision> history = new();
    private readonly Dictionary<(string Category, string Brand, string Field), string> defaults = new();
    public IReadOnlyList<FieldApplyDecision> Timeline => history.ToArray();
    public void SetDefault(string category, string brand, string field, string owner) { Validate(owner); defaults[(category, brand, field)] = owner; }
    public FieldSourceRule Configure(string productId, string field, string? owner = null, bool locked = false)
    { lock (gate) { var key = (productId, field); var selected = owner ?? "manual"; Validate(selected); var version = rules.TryGetValue(key, out var old) ? old.Version + 1 : 1; var item = new FieldSourceRule(productId, field, selected, locked, version); rules[key] = item; return item; } }
    public FieldApplyDecision Preview(string productId, string field, string source, string category = "", string brand = "")
    { lock (gate) { Validate(source); var key = (productId, field); var rule = rules.TryGetValue(key, out var r) ? r : Configure(productId, field, defaults.TryGetValue((category, brand, field), out var owner) ? owner : "manual"); var decision = rule.Locked && !source.Equals(rule.Owner, StringComparison.OrdinalIgnoreCase) ? "BLOCKED" : !source.Equals(rule.Owner, StringComparison.OrdinalIgnoreCase) && Priority(source) < Priority(rule.Owner) ? "SKIP" : "APPLY"; var result = new FieldApplyDecision(productId, field, source, decision, decision == "APPLY" ? $"source-of-truth={rule.Owner}" : $"owner={rule.Owner}; locked={rule.Locked}", rule.Version); history.Add(result); return result; } }
    public FieldApplyDecision ApplyApproved(FieldApplyDecision preview, long expectedVersion, bool approved) { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED"); lock (gate) { if (!rules.TryGetValue((preview.ProductId, preview.Field), out var rule) || rule.Version != expectedVersion) throw new InvalidOperationException("STALE_POLICY"); if (preview.Decision != "APPLY") throw new InvalidOperationException("FIELD_POLICY_BLOCKED"); return preview with { Decision = "APPLIED" }; } }
    public IReadOnlyList<FieldApplyDecision> PreviewBulk(IEnumerable<(string ProductId, string Field, string Source)> changes) => changes.Select(x => Preview(x.ProductId, x.Field, x.Source)).ToArray();
    private static int Priority(string source) => source.ToLowerInvariant() switch { "manual" => 5, "xml" => 4, "excel" => 3, "channel" => 2, "import" => 1, _ => 0 };
    private static void Validate(string source) { if (!Allowed.Contains(source, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Bilinmeyen source."); }
}
