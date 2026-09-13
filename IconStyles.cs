using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>Where an icon sits: beside body text, on a status surface, in a toolbar, or in the navigation rail.</summary>
public enum IconRole { Inline, Status, Toolbar, Navigation }

/// <summary>
/// The icon standard (#860). The app draws its icons as text glyphs, so an icon's size is a font size per role and
/// its alignment is the text's own baseline: a glyph beside a label is a run in the same block, never a separate
/// element that has to be nudged. An icon button gets a square minimum with centred content, the role's glyph size
/// when it carries only a glyph, a spoken name when a glyph is all it says (refused otherwise), and a visible
/// disabled state. Every number is device-independent; a 200 % display scales the host, not the icon.
/// </summary>
public static class IconStyles
{
    public const double DisabledOpacity = 0.55;

    public static double Size(IconRole role) => role switch
    {
        IconRole.Toolbar => DesignTokens.IconSizeToolbar,
        IconRole.Navigation => DesignTokens.IconSizeNavigation,
        _ => DesignTokens.IconSizeInline,
    };

    public static double ButtonMinSize(IconRole role) => role == IconRole.Toolbar ? DesignTokens.ToolbarButtonMinSize : DesignTokens.IconButtonMinSize;

    /// <summary>A glyph run that sits on the baseline of the text it is part of, sized for its role.</summary>
    public static Run GlyphRun(string glyph, IconRole role, Brush? brush = null)
    {
        var run = new Run(glyph ?? "") { FontSize = Size(role), BaselineAlignment = BaselineAlignment.Baseline };
        if (brush is not null) run.Foreground = brush;
        return run;
    }

    /// <summary>A glyph and its label in one block: one baseline by construction; the label keeps the block's size.</summary>
    public static TextBlock WithText(string glyph, string text, IconRole role = IconRole.Inline, Brush? glyphBrush = null)
    {
        var block = new TextBlock();
        block.Inlines.Add(GlyphRun(glyph, role, glyphBrush)); block.Inlines.Add(new Run(" ")); block.Inlines.Add(new Run(text ?? ""));
        return block;
    }

    /// <summary>A glyph on its own, centred in a square of its size.</summary>
    public static TextBlock Glyph(string glyph, IconRole role) => new()
    {
        Text = glyph ?? "", FontSize = Size(role), MinWidth = Size(role),
        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>
    /// Makes a button an icon button: a square minimum, centred content, the role's glyph size and compact padding when
    /// the content is a glyph alone (which then needs a spoken name), and a dimmed disabled state. A mixed glyph-and-
    /// label button keeps its body size and padding so its label matches the text around it.
    /// </summary>
    public static Button ApplyIconButton(Button button, IconRole role, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(button, name);
        var iconOnly = button.Content is string content && IsGlyphOnly(content);
        if (iconOnly && string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))) throw new ArgumentException("Yalnız simge taşıyan düğmenin erişilebilir adı gerekli.", nameof(button));
        if (iconOnly)
        {
            button.FontSize = Size(role);
            button.Padding = role == IconRole.Toolbar ? new Thickness(DesignTokens.SpaceControl, DesignTokens.SpaceInline, DesignTokens.SpaceControl, DesignTokens.SpaceInline) : Spacing.Chip;
        }
        var min = ButtonMinSize(role); button.MinWidth = min; button.MinHeight = min;
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        void Dim() => button.Opacity = button.IsEnabled ? 1 : DisabledOpacity;
        button.IsEnabledChanged += (_, _) => Dim(); Dim();
        return button;
    }

    static bool IsGlyphOnly(string content) { var t = content.Trim(); return t.Length > 0 && t.Length <= 2 && !t.Any(char.IsLetterOrDigit); }
}
