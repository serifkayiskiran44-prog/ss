using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The keyboard focus ring (#861), one for every control: a dark outer stroke and a light inner stroke drawn just
/// outside the control's bounds, so it reads on a teal button, a white page and the dark navigation rail alike;
/// pixel-snapped so a 2-DIP ring stays crisp at 150 %; the system's text and window colours under high contrast.
/// XAML controls take it through the token file's <c>KeyboardFocusVisual</c> style; code-built surfaces get a fresh
/// copy from <see cref="Create"/> (a Style belongs to the thread that made it, so the cached dictionary's copy is
/// never handed out). WPF draws a FocusVisualStyle only when focus arrived by keyboard: a mouse click keeps the
/// control's own state and the keyboard gets the ring. A disabled control cannot take focus at all.
/// </summary>
public static class FocusStyles
{
    public const string KeyboardFocusVisualKey = "KeyboardFocusVisual";

    public static Style Create(bool? highContrast = null)
    {
        var hc = highContrast ?? SeverityStyle.IsHighContrast;
        var outer = new FrameworkElementFactory(typeof(Rectangle));
        outer.SetValue(FrameworkElement.MarginProperty, DesignTokens.FocusRingMargin);
        outer.SetValue(Shape.StrokeThicknessProperty, DesignTokens.FocusRingThickness);
        outer.SetValue(Shape.StrokeProperty, hc ? SystemColors.WindowTextBrush : Frozen(DesignTokens.FocusRingColor));
        outer.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        outer.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var inner = new FrameworkElementFactory(typeof(Rectangle));
        inner.SetValue(Shape.StrokeThicknessProperty, DesignTokens.FocusRingInnerThickness);
        inner.SetValue(Shape.StrokeProperty, hc ? SystemColors.WindowBrush : Frozen(DesignTokens.FocusRingInnerColor));
        inner.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        inner.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(outer); grid.AppendChild(inner);
        var template = new ControlTemplate(typeof(Control)) { VisualTree = grid };
        template.Seal();
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    /// <summary>Gives an element the ring for the keyboard.</summary>
    public static T Apply<T>(T element) where T : FrameworkElement
    {
        ArgumentNullException.ThrowIfNull(element);
        element.FocusVisualStyle = Create();
        return element;
    }

    /// <summary>Makes a code-built surface (a row, a box) a keyboard stop with the ring.</summary>
    public static T MakeFocusable<T>(T element) where T : FrameworkElement
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Focusable = true; KeyboardNavigation.SetIsTabStop(element, true);
        return Apply(element);
    }

    /// <summary>Adds the ring to a code-built style (a grid's row or cell style) so its controls take it too.</summary>
    public static Style AddTo(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, Create()));
        return style;
    }

    static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
