using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record WorkspaceState(string Route, string ShopId, int PageSize, string Filter)
{
    public static WorkspaceState Default => new("dashboard", "", 50, "");
}

public static class WorkspaceStateCodec
{
    public static string Serialize(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(state.PageSize));
        return JsonSerializer.Serialize(state);
    }

    public static WorkspaceState Restore(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return WorkspaceState.Default;
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceState>(payload);
            return state is { Route.Length: > 0, PageSize: >= 1 and <= 500 } ? state : WorkspaceState.Default;
        }
        catch (JsonException) { return WorkspaceState.Default; }
    }
}
