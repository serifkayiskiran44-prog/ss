namespace TrMarketplaceHubDesktop.Catalog;

public sealed record WorkerTurnResult(string Cursor, bool Advanced, string Outcome);

public sealed class MarketplaceHubWorker(WorkerStateStore state, Func<string, WorkerEvidence> execute)
{
    public WorkerTurnResult RunOnce(DateTimeOffset nowUtc)
    {
        var current = state.Get();
        if (!state.TryClaim("marketplacehub-worker", nowUtc, TimeSpan.FromMinutes(2))) return new(current.Cursor, false, "LEASE_BUSY");
        var evidence = execute(current.Cursor);
        if (!evidence.CanAdvance) { state.Release("marketplacehub-worker", "EVIDENCE_BLOCKED"); return new(current.Cursor, false, "EVIDENCE_BLOCKED"); }
        var next = (int.Parse(current.Cursor) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var outcome = $"REAL_WORK_COUNT={evidence.RealWorkCount};TEST_RESULT={evidence.TestResult};PUBLISH={evidence.Publish}";
        state.Advance("marketplacehub-worker", next, outcome);
        return new(next, true, outcome);
    }
}
