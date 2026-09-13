using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public enum OverflowKind { ClippedText, ClippedControl }

/// <summary>One cut element: what kind, whose (the nearest owning control, its name or tag, and the element), the sanitized text, and the DIP it needed against the DIP it had.</summary>
public sealed record OverflowFinding(OverflowKind Kind, string Owner, string Text, double Needed, double Available, FrameworkElement Element)
{
    public override string ToString() => $"{Kind}: {Owner} \"{Text}\" needs {Needed:F0} DIP, has {Available:F0} DIP";
}

/// <summary>
/// The overflow audit (#867). It asks the layout itself: an element that WPF had to clip because its natural size
/// exceeded the slot it was given is a cut. A visible text block that is neither wrapped away nor trimmed by design
/// and got such a clip is cut text (the usual shape of a long Turkish label in a narrow button, header or cell); a
/// button, tab, header, check box or combo box that got one is a cut control. Wrapping, an ellipsis and a host that
/// scrolls are not findings. The numbers are device-independent.
/// </summary>
public static class OverflowAudit
{
    public const int TextLimit = 60;

    public static IReadOnlyList<OverflowFinding> Audit(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var findings = new List<OverflowFinding>();
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ClipToBounds) continue;
            if (element is TextBlock block)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.TextTrimming != TextTrimming.None) continue;
                if (IsCut(block, out var needed, out var available)) findings.Add(new OverflowFinding(OverflowKind.ClippedText, Owner(block), Safe(block.Text), needed, available, block));
            }
            else if (element is ButtonBase or TabItem or DataGridColumnHeader or ComboBox)
            {
                if (IsCut(element, out var needed, out var available)) findings.Add(new OverflowFinding(OverflowKind.ClippedControl, Owner(element), Safe(ContentText(element)), needed, available, element));
            }
        }
        return findings;
    }

    /// <summary>Whether the layout clipped the element: its render size against the slot it had (width first, then height for a wrapped text in a fixed-height row).</summary>
    public static bool IsCut(FrameworkElement element, out double needed, out double available)
    {
        needed = available = 0;
        if (LayoutInformation.GetLayoutClip(element) is null) return false;
        var slot = LayoutInformation.GetLayoutSlot(element); var margin = element.Margin;
        var availableWidth = Math.Max(0, slot.Width - margin.Left - margin.Right); var availableHeight = Math.Max(0, slot.Height - margin.Top - margin.Bottom);
        if (element.RenderSize.Width > availableWidth + 0.5) { needed = element.RenderSize.Width; available = availableWidth; return true; }
        if (element.RenderSize.Height > availableHeight + 0.5) { needed = element.RenderSize.Height; available = availableHeight; return true; }
        return false;
    }

    static string Owner(FrameworkElement element)
    {
        FrameworkElement? owner = null;
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control control) { owner = control; break; }
        owner ??= VisualTreeHelper.GetParent(element) as FrameworkElement;
        var label = owner is null ? "" : owner.GetType().Name + Identity(owner);
        return owner is null ? element.GetType().Name + Identity(element) : $"{label}/{element.GetType().Name}{Identity(element)}";
    }

    static string Identity(FrameworkElement element)
    {
        var id = !string.IsNullOrEmpty(element.Name) ? element.Name : element.Tag as string ?? "";
        return id.Length == 0 ? "" : $"({Safe(id)})";
    }

    static string ContentText(FrameworkElement element)
    {
        var block = Descendants(element).OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
        if (block is not null) return block.Text;
        return element is ContentControl { Content: string s } ? s : element is HeaderedContentControl { Header: string h } ? h : "";
    }

    static string Safe(string text)
    {
        var clean = AuditStore.Redact(text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= TextLimit ? clean : clean[..TextLimit] + "…";
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }
}
