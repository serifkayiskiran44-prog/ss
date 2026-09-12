namespace TrMarketplaceHubDesktop.Catalog;

public sealed record WorkerEvidence(int RealWorkCount, int VerificationOnlyCount, string FilesChanged, string TestsChanged, string TestResult, string Publish, string Blocker)
{
    public bool CanAdvance => RealWorkCount > 0 && VerificationOnlyCount == 0 && !string.IsNullOrWhiteSpace(FilesChanged) && !string.IsNullOrWhiteSpace(TestResult) && TestResult.Contains("pass", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Publish) && string.IsNullOrWhiteSpace(Blocker);
}
