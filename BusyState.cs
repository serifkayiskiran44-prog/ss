using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>One owner of the busy state: its key, the label it shows, whether it can be cancelled, how deeply it is nested.</summary>
public sealed record BusyOwner(string Key, string Label, bool CanCancel, int Depth);

/// <summary>
/// Global loading overlay deadlock guard (#885). The shell's busy state is reference-counted per owner: an operation
/// enters with its owner key, a label and (when it can be stopped) a cancel action, and leaves by disposing its
/// token — twice is once, an exception or a cancellation leaves through the same finally, nested entries of one
/// owner keep the overlay until the last one leaves, and a screen left while its load is in flight can drop its
/// owner outright. The overlay renders from this state alone, so it can only be open while an owner is in; when the
/// last owner leaves it closes, certainly. Its one action cancels the owners' tokens and runs nothing else — no
/// confirmation-gated action can be reached through it.
/// </summary>
public sealed class BusyState
{
    sealed class Entry { public string Label = ""; public Action? Cancel; public int Depth; public bool Cancelled; }
    readonly Dictionary<string, Entry> owners = new(StringComparer.Ordinal);
    readonly object gate = new();
    static readonly Regex KeyShape = new("^[A-Za-z0-9:_.-]{1,64}$", RegexOptions.Compiled);
    public const int MaxLabelLength = 120;
    public const string DefaultLabel = "İşlem sürüyor…";

    /// <summary>Raised after every change of the state, on the caller's thread.</summary>
    public event Action? Changed;

    public bool IsBusy { get { lock (gate) return owners.Count > 0; } }
    public int Depth { get { lock (gate) return owners.Values.Sum(e => e.Depth); } }
    public IReadOnlyList<BusyOwner> Owners { get { lock (gate) return owners.Select(p => new BusyOwner(p.Key, p.Value.Label, p.Value.Cancel is not null && !p.Value.Cancelled, p.Value.Depth)).ToList(); } }
    public bool CanCancel => Owners.Any(o => o.CanCancel);

    /// <summary>What the overlay says: the one owner's label, or the count and the latest label.</summary>
    public string Headline
    {
        get
        {
            var current = Owners;
            return current.Count == 0 ? "" : current.Count == 1 ? current[0].Label : $"{current.Count} işlem sürüyor · {current[^1].Label}";
        }
    }

    /// <summary>Enters an owner's scope; the same owner may nest; dispose the token to leave — a second dispose is a no-op.</summary>
    public IDisposable Enter(string owner, string? label, Action? cancel = null)
    {
        var key = SafeKey(owner);
        lock (gate)
        {
            if (!owners.TryGetValue(key, out var entry)) { entry = new Entry(); owners[key] = entry; }
            entry.Depth++; entry.Label = SafeLabel(label); entry.Cancel ??= cancel;
        }
        Changed?.Invoke();
        return new Token(this, key);
    }

    /// <summary>Runs work under an owner's scope: the scope closes on completion, exception or cancellation, whatever the work did.</summary>
    public async Task RunAsync(string owner, string? label, Func<Task> work, Action? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = Enter(owner, label, cancel);
        await work();
    }

    /// <summary>Cancels every owner that can be cancelled and returns how many were; runs nothing else.</summary>
    public int CancelAll()
    {
        List<Action> actions;
        lock (gate) { actions = owners.Values.Where(e => e.Cancel is not null && !e.Cancelled).Select(e => { e.Cancelled = true; return e.Cancel!; }).ToList(); }
        foreach (var action in actions) { try { action(); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); } }
        if (actions.Count > 0) Changed?.Invoke();
        return actions.Count;
    }

    /// <summary>Drops an owner entirely, however deeply it was nested — for a screen left while its load is in flight.</summary>
    public bool ReleaseAll(string owner)
    {
        bool removed; lock (gate) removed = owners.Remove(SafeKey(owner));
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>The terminal safety net: every owner released, the overlay closed.</summary>
    public int ReleaseEverything()
    {
        int count; lock (gate) { count = owners.Count; owners.Clear(); }
        if (count > 0) Changed?.Invoke();
        return count;
    }

    void Leave(string key)
    {
        bool changed; lock (gate) { changed = owners.TryGetValue(key, out var entry); if (changed && --entry!.Depth <= 0) owners.Remove(key); }
        if (changed) Changed?.Invoke();
    }

    public static string SafeKey(string? owner) => owner is not null && KeyShape.IsMatch(owner) ? owner : "unnamed";
    public static string SafeLabel(string? label)
    {
        var text = Regex.Replace(AuditStore.Redact(label ?? ""), @"\s+", " ").Trim();
        if (text.Length == 0) return DefaultLabel;
        return text.Length <= MaxLabelLength ? text : text[..(MaxLabelLength - 1)] + "…";
    }

    sealed class Token : IDisposable
    {
        readonly BusyState state; readonly string key; int disposed;
        public Token(BusyState state, string key) { this.state = state; this.key = key; }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 1) return; state.Leave(key); }
    }
}

/// <summary>The global loading overlay: a veil over the workspace with the busy headline, an indeterminate bar and one action — cancel — that only cancels the owners' tokens.</summary>
public static class BusyOverlay
{
    public const string Tag = "busy-overlay";
    public const string LabelTag = "busy-overlay-label";
    public const string CancelTag = "busy-overlay-cancel";
    public const string CancelLabel = "İptal";

    public static Grid Create(BusyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var overlay = new Grid { Tag = Tag, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(140, 243, 245, 249)), Focusable = true, IsHitTestVisible = true };
        Panel.SetZIndex(overlay, 9);
        var body = new StackPanel { MinWidth = 280, MaxWidth = 520 };
        var label = new TextBlock { Tag = LabelTag, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(0, 10, 0, 10) };
        var cancel = new Button { Content = CancelLabel, Tag = CancelTag, HorizontalAlignment = HorizontalAlignment.Right, MinHeight = DesignTokens.HitTargetMinSize, Padding = new Thickness(14, 3, 14, 3), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(cancel, "Süren işlemi iptal et");
        cancel.Click += (_, _) => state.CancelAll();
        body.Children.Add(label); body.Children.Add(bar); body.Children.Add(cancel);
        var card = new Border { Child = body, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(206, 220, 229)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 14, 18, 14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        overlay.Children.Add(card);
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && state.CanCancel) { state.CancelAll(); e.Handled = true; } };
        void Render()
        {
            var busy = state.IsBusy;
            overlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            label.Text = state.Headline;
            cancel.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(overlay, busy ? "İşlem sürüyor: " + state.Headline : "İşlem yok");
        }
        state.Changed += () => { if (overlay.Dispatcher.CheckAccess()) Render(); else overlay.Dispatcher.BeginInvoke(new Action(Render)); };
        Render();
        return overlay;
    }
}
