namespace TrMarketplaceHubDesktop;

public sealed record AgentSkillEvaluation(string Scenario, string Decision, string Evidence);

public static class AgentSkillEvaluationMatrix
{
    public static IReadOnlyList<AgentSkillEvaluation> Representative { get; } =
    [new("product-ui-parity", "KEEP", "evidence gate prevents unsupported P0 acceptance"), new("connector-contract", "KEEP", "official-only capability rule"), new("xml-supplier-anomaly", "KEEP", "provenance/source safety rule"), new("sqlite-migration", "KEEP", "isolated recovery rule"), new("release-acceptance", "KEEP", "artifact/test evidence gate"), new("low-risk-local-fix", "MERGE", "avoid unnecessary reviewer fan-out")];
}
