using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

/// <summary>What is about to be destroyed, in the operator's terms: the verb, the scope, and how many records.</summary>
public sealed record DestructiveIntent(string Verb, string Scope, int AffectedCount, bool IsLocalOnly = true, string Noun = "ürün");

public sealed record DestructiveVerdict(bool Allowed, string Reason)
{
    public static readonly DestructiveVerdict Ok = new(true, "");
}

/// <summary>
/// Typed confirmation for high-impact local destructive actions (#821). A Yes/No box is answered by reflex; a
/// box that asks the operator to type the affected count is answered by reading it. The rules: the phrase to
/// type is the count itself (language-neutral, and it forces the number to be read); a single record needs no
/// typing -- the ordinary destructive confirm (#818, Enter on Cancel) is enough; the count is checked again at
/// the moment of confirmation, and a count that moved since the dialog opened refuses with both numbers, because
/// the operator confirmed what they saw, not what is there now. And the security line: this pattern is for
/// <b>local</b> data only -- a live marketplace write is refused outright here, because its guards are preview,
/// explicit approval, revision, idempotency and the wrong-store check, and a typed word must never stand in for
/// them.
/// </summary>
public static class DestructiveConfirmation
{
    /// <summary>From this many records on, the count must be typed.</summary>
    public const int TypedThreshold = 2;
    public const string LiveWriteRefused = "Canlı pazaryeri yazımı bu onayla yapılmaz: önizleme, açık onay, sürüm ve idempotency koruması gerekir.";

    public static bool RequiresTyping(DestructiveIntent intent) => intent.AffectedCount >= TypedThreshold;

    /// <summary>The exact text the operator must type: the count, as digits.</summary>
    public static string Phrase(DestructiveIntent intent) => intent.AffectedCount.ToString(CultureInfo.InvariantCulture);

    public static string Title(DestructiveIntent intent) => $"{intent.AffectedCount.ToString("N0", CultureInfo.CurrentCulture)} {intent.Noun} {intent.Verb.ToLower(CultureInfo.GetCultureInfo("tr-TR"))}";

    public static string Message(DestructiveIntent intent) =>
        $"{intent.AffectedCount.ToString("N0", CultureInfo.CurrentCulture)} {intent.Noun} {intent.Scope} kapsamında {intent.Verb.ToLower(CultureInfo.GetCultureInfo("tr-TR"))}. Bu işlem geri alınamaz. Onaylamak için etkilenen sayıyı yazın: {Phrase(intent)}";

    /// <summary>The decision at the moment of confirmation: typed text, and the count as it is right now.</summary>
    public static DestructiveVerdict Validate(DestructiveIntent intent, string? typed, int currentCount)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.IsLocalOnly) return new(false, LiveWriteRefused);
        if (currentCount != intent.AffectedCount)
            return new(false, $"Etkilenen sayı değişti: {intent.AffectedCount.ToString("N0", CultureInfo.CurrentCulture)} → {currentCount.ToString("N0", CultureInfo.CurrentCulture)}. Yeniden önizleyip tekrar onaylayın.");
        if (!RequiresTyping(intent)) return DestructiveVerdict.Ok;
        return string.Equals((typed ?? "").Trim(), Phrase(intent), StringComparison.Ordinal)
            ? DestructiveVerdict.Ok
            : new(false, $"Onay metni eşleşmedi; tam olarak {Phrase(intent)} yazın.");
    }
}

/// <summary>The typed-confirmation dialog on the standard shell (#818).</summary>
public static class DestructiveConfirmDialog
{
    /// <summary>
    /// Shows the confirmation and returns true only when the verdict allowed it. <paramref name="currentCount"/>
    /// is evaluated at the click, so a stale count refuses inside the dialog and leaves it open to be read.
    /// </summary>
    public static bool Show(Window? owner, DestructiveIntent intent, Func<int> currentCount)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(currentCount);
        if (!intent.IsLocalOnly) throw new InvalidOperationException(DestructiveConfirmation.LiveWriteRefused);
        if (!DestructiveConfirmation.RequiresTyping(intent))
            return DialogShell.Confirm(owner, DestructiveConfirmation.Title(intent), DestructiveConfirmation.Message(intent), intent.Verb);
        var window = Build(owner, intent, currentCount, out _);
        return window.ShowDialog() == true;
    }

    /// <summary>Builds the dialog without showing it, so a test can drive the box and the buttons.</summary>
    public static Window Build(Window? owner, DestructiveIntent intent, Func<int> currentCount, out TextBox typed)
    {
        var body = new StackPanel();
        body.Children.Add(DialogShell.Message(DestructiveConfirmation.Message(intent)));
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 0), MaxLength = 12 };
        AutomationProperties.SetName(box, "Onay metni");
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Foreground = SeverityStyle.AccentBrush(SeverityLevel.Blocking, SeverityStyle.IsHighContrast) };
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Assertive);
        body.Children.Add(box); body.Children.Add(feedback);
        Button? confirm = null;
        var window = DialogShell.Create(owner, DestructiveConfirmation.Title(intent), body, new DialogShell.Action[]
        {
            new("Vazgeç", IsCancel: true),
            new(intent.Verb, IsPrimary: true, OnClick: () =>
            {
                var verdict = DestructiveConfirmation.Validate(intent, box.Text, currentCount());
                if (verdict.Allowed) return true;
                feedback.Text = verdict.Reason; feedback.Visibility = Visibility.Visible;
                if (confirm is not null) confirm.IsEnabled = false;
                box.Focus(); box.SelectAll();
                return false;
            }),
        }, 480, 300, destructive: true);
        // The confirm button is Enter only once the phrase matches; before that Enter does nothing.
        var bar = ((DockPanel)window.Content).Children.OfType<StackPanel>().Single();
        confirm = bar.Children.OfType<Button>().Single(b => (string)b.Content == intent.Verb);
        confirm.IsEnabled = false; confirm.IsDefault = false;
        var cancel = bar.Children.OfType<Button>().Single(b => b.IsCancel);
        cancel.IsDefault = false;
        box.TextChanged += (_, _) =>
        {
            var matches = string.Equals(box.Text.Trim(), DestructiveConfirmation.Phrase(intent), StringComparison.Ordinal);
            confirm.IsEnabled = matches; confirm.IsDefault = matches;
            if (matches) feedback.Visibility = Visibility.Collapsed;
        };
        window.Loaded += (_, _) => box.Focus();
        typed = box;
        return window;
    }
}
