using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

/// <summary>The built secret row: the masked box, the presence line, and the two things a form needs -- whether a saved value exists and what to save.</summary>
public sealed class SecretFieldControl
{
    public required FormFieldControl Field { get; init; }
    public required PasswordBox Box { get; init; }
    public required TextBlock Presence { get; init; }
    public bool HasSaved { get; private set; }

    /// <summary>Whether the operator typed something new in this session.</summary>
    public bool HasTyped => Box.Password.Length > 0;

    /// <summary>Tells the row whether a saved value exists; the value itself never comes here.</summary>
    public void SetSaved(bool present)
    {
        HasSaved = present;
        Presence.Text = present ? SecretField.SavedText : SecretField.EmptyText;
        AutomationProperties.SetHelpText(Box, (Field.HelpText.Text.Length > 0 ? Field.HelpText.Text + " " : "") + Presence.Text);
    }

    /// <summary>The value to save: what was typed, or the saved one when the box was left alone -- never an empty overwrite by accident.</summary>
    public string Resolve(string? saved) => HasTyped ? Box.Password : (saved ?? "");

    /// <summary>After a successful save: the typed value is dropped from the box and the row says a value exists.</summary>
    public void MarkSaved() { Box.Clear(); SetSaved(true); }

    /// <summary>After a delete: nothing typed, nothing saved.</summary>
    public void MarkCleared() { Box.Clear(); SetSaved(false); }
}

/// <summary>
/// The secret input standard (#855): a masked box that never shows or copies its content, that trims a pasted value
/// to one clean line (a key copied with a trailing newline or a surrounding quote is the classic failure), that
/// shows only whether a saved value exists -- the saved secret is never loaded into the box -- and that keeps the
/// saved value when the box is left empty on save. Built on the form-row standard, so it is labelled, has help and a
/// validation slot, and reads correctly to a screen reader. What is typed flows to the store and nowhere else.
/// </summary>
public static class SecretField
{
    public const string SavedText = "Kayıtlı gizli değer var. Değiştirmek için yeni değeri yazın; boş bırakılırsa mevcut değer korunur.";
    public const string EmptyText = "Kayıtlı gizli değer yok.";
    public const int MaxLength = 512;

    public static SecretFieldControl Build(string label, string help = "", bool required = true)
    {
        var box = new PasswordBox { MaxLength = MaxLength };
        var field = FormField.Build(new FormFieldSpec(label, required, help), box);
        var presence = new TextBlock { Tag = "secret-presence", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.85, Margin = new Thickness(0, FormField.HelpGap, 0, 0) };
        field.Root.Children.Insert(field.Root.Children.IndexOf(field.HelpText) + 1, presence);
        // Copy and cut are refused outright; a PasswordBox has no reveal, so the value leaves only through Resolve.
        CommandManager.AddPreviewCanExecuteHandler(box, (_, e) => { if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut) { e.CanExecute = false; e.ContinueRouting = false; e.Handled = true; } });
        CommandManager.AddPreviewExecutedHandler(box, (_, e) => { if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut) e.Handled = true; });
        // A paste is cleaned to one line: surrounding whitespace, quotes and a trailing newline go; a multi-line dump keeps its first non-empty line.
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
            var clean = Clean(e.DataObject.GetData(DataFormats.UnicodeText) as string);
            e.CancelCommand();
            if (clean.Length > 0) box.Password = clean; // setting Password raises PasswordChanged, so the form's own change tracking runs
        });
        var control = new SecretFieldControl { Field = field, Box = box, Presence = presence };
        control.SetSaved(false);
        return control;
    }

    /// <summary>One clean line out of whatever was pasted: trimmed, unquoted, the first non-empty line, capped.</summary>
    public static string Clean(string? pasted)
    {
        var text = (pasted ?? "").Replace("\r", "");
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length >= 2 && ((line[0] == '"' && line[^1] == '"') || (line[0] == '\'' && line[^1] == '\''))) line = line[1..^1].Trim();
        return line.Length > MaxLength ? line[..MaxLength] : line;
    }
}
