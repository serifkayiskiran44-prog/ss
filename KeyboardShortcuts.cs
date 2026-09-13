using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

/// <summary>Where a shortcut applies: the shell (any page) or the product grid and its inspect drawer.</summary>
public enum ShortcutScope { Shell, Products }

/// <summary>One real keyboard command of the shell: what it does, where, which keys, and the condition under which it does anything.</summary>
public sealed record Shortcut(string CommandKey, string Label, ShortcutScope Scope, Key Key, ModifierKeys Modifiers, string? Condition = null, bool Destructive = false)
{
    public string Gesture => KeyboardShortcuts.Gesture(Key, Modifiers);
    public bool Matches(Key key, ModifierKeys modifiers) => modifiers == Modifiers && (key == Key || KeyboardShortcuts.Alias(key) == Key);
    public override string ToString() => Condition is null ? $"{Label} — {Gesture}" : $"{Label} — {Gesture} ({Condition})";
}

/// <summary>
/// The one catalogue of the shell's keyboard commands (#869). The key handler, the product grid's bindings, the
/// tooltip hints and the searchable reference all read it, so a gesture is written once and read the way a person
/// reads it. A shortcut exists only for an action the shell has; nothing destructive and nothing that writes to a
/// marketplace has one, and no shortcut steps around a confirmation.
/// </summary>
public static class KeyboardShortcuts
{
    public static IReadOnlyList<Shortcut> Catalogue { get; } = new Shortcut[]
    {
        new("global-search", "Genel arama kutusuna git", ShortcutScope.Shell, Key.K, ModifierKeys.Control),
        new("navigate-dashboard", "Gösterge paneline git", ShortcutScope.Shell, Key.D1, ModifierKeys.Control),
        new("navigate-products", "Ürünlere git", ShortcutScope.Shell, Key.D2, ModifierKeys.Control),
        new("toggle-sidebar", "Menüyü daralt veya genişlet", ShortcutScope.Shell, Key.B, ModifierKeys.Control),
        new("back", "Geri (gezinti izinde bir adım)", ShortcutScope.Shell, Key.Left, ModifierKeys.Alt, "iz varken"),
        new("refresh-products", "Ürün listesini yenile", ShortcutScope.Shell, Key.F5, ModifierKeys.None),
        new("dismiss-toast", "Bildirimi kapat", ShortcutScope.Shell, Key.Escape, ModifierKeys.None, "bildirim odaktayken"),
        new("shortcut-reference", "Klavye kısayolları listesi", ShortcutScope.Shell, Key.F1, ModifierKeys.None),
        new("product-inspect", "Seçili ürünü hızlı incele", ShortcutScope.Products, Key.I, ModifierKeys.Control),
        new("close-inspect", "Hızlı incelemeyi kapat", ShortcutScope.Products, Key.Escape, ModifierKeys.None, "inceleme açıkken"),
    };

    public static Shortcut? Find(string commandKey) => Catalogue.FirstOrDefault(s => string.Equals(s.CommandKey, commandKey, StringComparison.Ordinal));

    /// <summary>The catalogue entry a key press means in a scope, or null: the number pad's digits count as the digits.</summary>
    public static Shortcut? Match(Key key, ModifierKeys modifiers, ShortcutScope scope) => Catalogue.FirstOrDefault(s => s.Scope == scope && s.Matches(key, modifiers));

    /// <summary>A key that means the same as another for a shortcut: the number pad's digits.</summary>
    public static Key Alias(Key key) => key >= Key.NumPad0 && key <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;

    /// <summary>The gesture the way a person reads it: "Ctrl+K", "Alt+←", "F5", "Esc".</summary>
    public static string Gesture(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            Key.Escape => "Esc", Key.Enter => "Enter", Key.Space => "Boşluk", Key.Tab => "Tab", Key.Delete => "Delete", Key.Back => "Backspace",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>A label with its gesture appended for a tooltip or a menu: "Geri (Alt+←)"; an action without a shortcut keeps its plain label.</summary>
    public static string Hint(string label, string commandKey) => Find(commandKey) is { } s ? $"{label} ({s.Gesture})" : label;

    /// <summary>The entries a query finds, on the label, the gesture and the condition, with the Turkish-safe fold; an empty query lists everything.</summary>
    public static IReadOnlyList<Shortcut> Filter(string? query) => Catalogue.Where(s => UiSearch.Matches($"{s.Label} {s.Gesture} {s.Condition}", query)).ToList();

    /// <summary>Two commands that share a gesture in one scope; the catalogue must have none.</summary>
    public static IReadOnlyList<(Shortcut First, Shortcut Second)> Conflicts()
    {
        var conflicts = new List<(Shortcut, Shortcut)>();
        for (var i = 0; i < Catalogue.Count; i++)
            for (var j = i + 1; j < Catalogue.Count; j++)
                if (Catalogue[i].Scope == Catalogue[j].Scope && Catalogue[i].Gesture == Catalogue[j].Gesture) conflicts.Add((Catalogue[i], Catalogue[j]));
        return conflicts;
    }

    /// <summary>A key binding for a control, from the catalogue, so the binding and the reference cannot drift apart.</summary>
    public static KeyBinding Binding(string commandKey, ICommand command)
    {
        var s = Find(commandKey) ?? throw new ArgumentException($"Kısayol kataloğunda '{commandKey}' yok.", nameof(commandKey));
        return new KeyBinding(command, s.Key, s.Modifiers);
    }
}
