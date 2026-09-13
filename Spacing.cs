using System.Windows;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The spacing scale in code (#859): every layout margin a panel needs, derived from the token file's four-step
/// scale (inline 4, control 8, section 12, page 20, plus the 2-DIP hairline) -- the page, section, inline and
/// control margins, the directional gaps between stacked or inline pieces, the chip padding and the three
/// text-block rhythms the shared Heading/Hint/Text helpers use. A panel says what a gap is for, not how many
/// pixels it is; the numbers are device-independent, so a 200 % display scales the host, not the rhythm.
/// </summary>
public static class Spacing
{
    public static Thickness Page => DesignTokens.PageMargin;
    public static Thickness Section => DesignTokens.SectionMargin;
    public static Thickness Inline => DesignTokens.InlineMargin;
    public static Thickness Control => DesignTokens.ControlMargin;
    public static Thickness Chip => DesignTokens.ChipPadding;
    public static Thickness TitleBlock => DesignTokens.TitleBlockMargin;
    public static Thickness HintBlock => DesignTokens.HintBlockMargin;
    public static Thickness BodyBlock => DesignTokens.BodyBlockMargin;

    public static Thickness BelowSection => new(0, 0, 0, DesignTokens.SpaceSection);
    public static Thickness BelowControl => new(0, 0, 0, DesignTokens.SpaceControl);
    public static Thickness BelowInline => new(0, 0, 0, DesignTokens.SpaceInline);
    public static Thickness AboveInline => new(0, DesignTokens.SpaceInline, 0, 0);
    public static Thickness AboveControl => new(0, DesignTokens.SpaceControl, 0, 0);
    public static Thickness VerticalControl => new(0, DesignTokens.SpaceControl, 0, DesignTokens.SpaceControl);
    public static Thickness RightInline => new(0, 0, DesignTokens.SpaceInline, 0);
    public static Thickness LeftControl => new(DesignTokens.SpaceControl, 0, 0, 0);
}
