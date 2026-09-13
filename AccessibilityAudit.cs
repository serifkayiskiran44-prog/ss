using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public enum AccessibilityIssue { Unnamed, Unreachable, FocusTrap, NoFocusVisual, DisabledWithoutReason }

/// <summary>One accessibility defect: what kind, whose (type and identity, sanitized), and a short detail.</summary>
public sealed record AccessibilityFinding(AccessibilityIssue Issue, string Owner, string Detail)
{
    public override string ToString() => Detail.Length > 0 ? $"{Issue}: {Owner} — {Detail}" : $"{Issue}: {Owner}";
}

/// <summary>A Tab walk through the active window: the stops in order and whether the cycle came back to its start.</summary>
public sealed record KeyboardWalk(IReadOnlyList<FrameworkElement> Stops, bool Closed);

/// <summary>
/// The accessibility audit (#888): what a keyboard or screen-reader user meets on a screen, asked of the tree itself.
/// A keyboard unit is an input or command (a button, a box, a combo, a date picker, a slider), a code-built surface
/// made a keyboard stop (#861), or a container a keyboard user enters as one stop and then moves through with the
/// arrow keys (a grid, a list, a tab strip). Every input and surface must announce a name (#819 form fields, #860
/// icon buttons); every enabled unit must be reachable by Tab, and the Tab cycle must come back to where it
/// started (a trap is a screen a keyboard user cannot leave); every focusable input must keep a focus visual; and a
/// disabled input must explain itself (#871) through a reason or a tooltip that shows while disabled. Chrome inside a
/// control's template (a grid's select-all corner, a combo's toggle, a date picker's inner box) is the control's own
/// business and is not judged on its own; the items inside a grid or list are arrow-key territory and are not
/// expected as Tab stops.
/// </summary>
public static class AccessibilityAudit
{
    public const int WalkLimit = 800;
    const int OwnerTextLimit = 40;

    public static bool IsInput(FrameworkElement element) => element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or DatePicker or Slider;
    public static bool IsContainer(FrameworkElement element) => element is DataGrid or ListBox or TabControl or TreeView;
    /// <summary>A code-built surface (a row, a card, a box) that was made a keyboard stop.</summary>
    public static bool IsSurface(FrameworkElement element) => element is not Control && element.Focusable && KeyboardNavigation.GetIsTabStop(element);
    static bool IsChrome(FrameworkElement element) => element.TemplatedParent is not null and not ContentPresenter;

    /// <summary>The keyboard units under a root, in document order.</summary>
    public static IReadOnlyList<FrameworkElement> Units(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Descendants(root).OfType<FrameworkElement>().Where(e => e.IsVisible && !IsChrome(e) && (IsInput(e) || IsSurface(e) || IsContainer(e))).ToList();
    }

    /// <summary>The name assistive technology announces: the automation name, the labelling element's text, the content's text, or a text tooltip; empty when there is none.</summary>
    public static string AccessibleName(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var name = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        if (AutomationProperties.GetLabeledBy(element) is { } label) { var text = TextOf(label); if (text.Length > 0) return text; }
        if (element is ContentControl { Content: var content })
        {
            var text = content is string s ? s : content is DependencyObject d ? TextOf(d) : "";
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        if (element.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)) return tip.Trim();
        return "";
    }

    /// <summary>Inputs and surfaces with no name to announce.</summary>
    public static IReadOnlyList<AccessibilityFinding> Names(FrameworkElement root)
        => Units(root).Where(e => (IsInput(e) || IsSurface(e)) && AccessibleName(e).Length == 0).Select(e => new AccessibilityFinding(AccessibilityIssue.Unnamed, Owner(e), "")).ToList();

    /// <summary>Focusable inputs and surfaces whose focus visual was switched off.</summary>
    public static IReadOnlyList<AccessibilityFinding> FocusVisuals(FrameworkElement root)
        => Units(root).Where(e => (IsInput(e) || IsSurface(e)) && e.Focusable && e.FocusVisualStyle is null).Select(e => new AccessibilityFinding(AccessibilityIssue.NoFocusVisual, Owner(e), "")).ToList();

    /// <summary>Disabled inputs that say nothing about why — no reason on them or an ancestor, no tooltip that shows while disabled.</summary>
    public static IReadOnlyList<AccessibilityFinding> DisabledReasons(FrameworkElement root)
        => Units(root).Where(e => IsInput(e) && !e.IsEnabled && !Explained(e)).Select(e => new AccessibilityFinding(AccessibilityIssue.DisabledWithoutReason, Owner(e), "")).ToList();

    static bool Explained(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (CommandState.ReasonOf(node) is not null) return true;
            if (node is FrameworkElement f && ToolTipService.GetShowOnDisabled(f) && f.ToolTip is string tip && tip.Trim().Length > 0) return true;
        }
        return false;
    }

    /// <summary>Walks Tab from the first enabled unit under the root, through the whole window, until the cycle comes back to its start; the window must be the active one.</summary>
    public static KeyboardWalk Walk(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var stops = new List<FrameworkElement>();
        var start = Units(root).FirstOrDefault(u => u.IsEnabled);
        if (start is null) return new KeyboardWalk(stops, true);
        var entered = start.Focusable && KeyboardNavigation.GetIsTabStop(start) ? start.Focus() : start.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        if (!entered || Keyboard.FocusedElement is not FrameworkElement first) return new KeyboardWalk(stops, false);
        stops.Add(first);
        for (var i = 0; i < WalkLimit; i++)
        {
            if (Keyboard.FocusedElement is not FrameworkElement current || !current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) return new KeyboardWalk(stops, false);
            if (Keyboard.FocusedElement is not FrameworkElement next || ReferenceEquals(next, current)) return new KeyboardWalk(stops, false);
            if (ReferenceEquals(next, first)) return new KeyboardWalk(stops, true);
            stops.Add(next);
        }
        return new KeyboardWalk(stops, false);
    }

    /// <summary>Enabled units the walk never reached (itself or through a descendant), and the trap when the cycle did not close.</summary>
    public static IReadOnlyList<AccessibilityFinding> Reachability(FrameworkElement root, KeyboardWalk walk)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(walk);
        var findings = new List<AccessibilityFinding>();
        if (!walk.Closed) findings.Add(new AccessibilityFinding(AccessibilityIssue.FocusTrap, walk.Stops.Count > 0 ? Owner(walk.Stops[^1]) : Owner(root), $"the Tab cycle did not come back after {walk.Stops.Count} stops"));
        var reached = new HashSet<DependencyObject>(walk.Stops);
        // An empty grid or list is skipped by Tab (there is no cell to enter); it becomes a stop when it has items.
        foreach (var unit in Units(root).Where(u => u.IsEnabled && !InsideList(u) && !(u is ItemsControl { Items.Count: 0 } and not TabControl)))
            if (!reached.Contains(unit) && !Descendants(unit).Any(reached.Contains)) findings.Add(new AccessibilityFinding(AccessibilityIssue.Unreachable, Owner(unit), ""));
        return findings;
    }

    public static IReadOnlyList<AccessibilityFinding> Audit(FrameworkElement root, KeyboardWalk? walk = null)
        => Names(root).Concat(FocusVisuals(root)).Concat(DisabledReasons(root)).Concat(Reachability(root, walk ?? Walk(root))).ToList();

    static bool InsideList(FrameworkElement element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is DataGrid or ListBox or TreeView) return true;
        return false;
    }

    /// <summary>Type plus identity — the name, the tag, the announced name or the content — sanitized and short.</summary>
    public static string Owner(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var identity = element.Name is { Length: > 0 } n ? n : element.Tag is string t && t.Length > 0 ? t : AccessibleName(element);
        if (identity.Length == 0 && element is ContentControl c && c.Content is DependencyObject d) identity = TextOf(d);
        if (identity.Length == 0 && element is DataGrid grid) identity = string.Join(" | ", grid.Columns.Take(3).Select(col => col.Header?.ToString() ?? "")) + $" ({grid.Items.Count} satır)";
        if (identity.Length == 0 && element is ItemsControl items) identity = $"{items.Items.Count} öğe";
        identity = Short(identity);
        var context = Context(element);
        var owner = identity.Length > 0 ? $"{element.GetType().Name} \"{identity}\"" : element.GetType().Name;
        return context.Length > 0 ? $"{owner} after \"{context}\"" : owner;
    }

    /// <summary>The nearest text that precedes the element in its panel, or the panel's own identity — where an unnamed control sits.</summary>
    static string Context(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (VisualTreeHelper.GetParent(node) is not Panel panel) continue;
            var index = panel.Children.IndexOf((UIElement)node);
            for (var i = index - 1; i >= 0; i--)
            {
                var text = panel.Children[i] is TextBlock block ? Text(block) : panel.Children[i] is DependencyObject sibling ? TextOf(sibling) : "";
                if (text.Length > 0) return Short(text);
            }
            if (panel.Name is { Length: > 0 } || panel.Tag is string) return Short(panel.Name is { Length: > 0 } ? panel.Name : (string)panel.Tag!);
        }
        return "";
    }

    static string Short(string text)
    {
        var clean = AuditStore.Redact(text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > OwnerTextLimit ? clean[..OwnerTextLimit] + "…" : clean;
    }

    static string TextOf(DependencyObject node)
        => node is TextBlock block ? Text(block) : string.Join(" ", Descendants(node).OfType<TextBlock>().Select(Text).Where(t => t.Length > 0));

    static string Text(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text.Trim();

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }
}
