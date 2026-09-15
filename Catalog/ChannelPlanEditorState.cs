namespace TrMarketplaceHubDesktop.Catalog;

/// The channel-plan editor's field values, captured as an immutable snapshot so
/// "did the user actually change anything since the plan was (re)loaded" can be
/// answered by pure structural equality - never by watching individual
/// TextChanged events fire (those also fire when LoadPlan populates the fields
/// programmatically, which must never itself count as a user edit). See #2546.
public sealed record ChannelPlanEditorFields(string ListingId, string ListingUrl, string TargetCategory, string Price, string Currency, string Stock, string Notes);

/// What a navigation-away action (selection change, shop reload, search
/// refresh, paging, window close) should do given the editor's dirty state and
/// the user's choice in the Save/Discard/Cancel guard.
public enum ChannelPlanNavigationDecision { Proceed, Cancel }

public static class ChannelPlanEditorState
{
    /// True only when the current fields differ from the baseline captured the
    /// last time LoadPlan populated the editor (a fresh load, or right after a
    /// successful Save's own readback) - never when they're still identical,
    /// even if the user typed something and then typed it back.
    public static bool IsDirty(ChannelPlanEditorFields baseline, ChannelPlanEditorFields current) => baseline != current;
}
