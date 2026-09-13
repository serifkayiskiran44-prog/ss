using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>The text roles a screen is made of; each maps to a size, a weight and a face in the token file.</summary>
public enum TextRole { PageTitle, SectionTitle, SubsectionTitle, Body, Hint, Caption, Kpi, Mono }

/// <summary>
/// The typography scale in code (#858): the same roles the token file exposes as styles for XAML. <see cref="Apply"/>
/// sets only the type -- size, weight, face, and wrapping when the block has no say -- and leaves the caller's text,
/// margin and colour alone; the two muted roles (hint, caption) take the design grey, and under high contrast the
/// system's grey-text colour, so a hint never vanishes on a black ground. Sizes are device-independent numbers:
/// a 200 % display scales the host, not the tokens.
/// </summary>
public static class TextStyles
{
    // #862: the muted colour is a token the contrast audit checks (4.5:1 on white); a frozen brush is safe on every thread.
    public static Color MutedColor => DesignTokens.TextMutedColor;
    static Brush? muted;
    static Brush Muted => muted ??= Frozen(MutedColor);
    static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    public static double Size(TextRole role) => role switch
    {
        TextRole.PageTitle => DesignTokens.TextPageTitleSize,
        TextRole.SectionTitle => DesignTokens.TextSectionTitleSize,
        TextRole.SubsectionTitle => DesignTokens.TextSubsectionTitleSize,
        TextRole.Caption => DesignTokens.TextCaptionSize,
        TextRole.Kpi => DesignTokens.TextKpiSize,
        TextRole.Mono => DesignTokens.TextMonoSize,
        _ => DesignTokens.TextBodySize,
    };

    public static FontWeight Weight(TextRole role) => role switch
    {
        TextRole.PageTitle or TextRole.SectionTitle or TextRole.SubsectionTitle => DesignTokens.FontWeightTitle,
        TextRole.Kpi => DesignTokens.FontWeightKpi,
        _ => FontWeights.Normal,
    };

    public static FontFamily Family(TextRole role) => role == TextRole.Mono ? DesignTokens.FontFamilyMono : DesignTokens.FontFamilyBody;

    /// <summary>Gives a block its role's type; text, margin and a colour the caller set stay. Muted roles get their colour here.</summary>
    public static TextBlock Apply(TextBlock block, TextRole role, bool? highContrast = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.FontSize = Size(role); block.FontWeight = Weight(role); block.FontFamily = Family(role);
        if (block.ReadLocalValue(TextBlock.TextWrappingProperty) == DependencyProperty.UnsetValue) block.TextWrapping = TextWrapping.Wrap;
        if (role is TextRole.Hint or TextRole.Caption) block.Foreground = (highContrast ?? SeverityStyle.IsHighContrast) ? SystemColors.GrayTextBrush : Muted;
        return block;
    }

    public static TextBlock Create(TextRole role, string text) => Apply(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, role);

    /// <summary>A code or diagnostics input: the monospace face and size, nothing else touched.</summary>
    public static T ApplyMono<T>(T input) where T : Control
    {
        ArgumentNullException.ThrowIfNull(input);
        input.FontFamily = DesignTokens.FontFamilyMono; input.FontSize = DesignTokens.TextMonoSize; return input;
    }
}
