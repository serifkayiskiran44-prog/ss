using System;
using System.Linq;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #869 (DESIGN: Keyboard shortcut discoverability). One catalogue of the shell's real keyboard commands, from which
// the key handler, the tooltip hints and the searchable reference all read: a gesture is written the way a person
// reads it, no two commands share a gesture in one scope, a shortcut exists only for an action the shell has (no
// Delete, nothing destructive or live-writing), and the reference filters with the Turkish-safe fold.
[TestClass]
public sealed class KeyboardShortcutsTests
{
    [TestMethod]
    public void TheCatalogueIsConflictFreeReadableSearchableAndNeverDestructive()
    {
        var all = KeyboardShortcuts.Catalogue;
        Assert.IsTrue(all.Count >= 8, "the shell's real commands");
        foreach (var s in all) { Assert.IsFalse(string.IsNullOrWhiteSpace(s.Label), s.CommandKey); Assert.IsFalse(string.IsNullOrWhiteSpace(s.Gesture), s.CommandKey); Assert.IsFalse(s.Destructive, $"{s.CommandKey} must not be destructive"); }
        Assert.AreEqual(all.Count, all.Select(s => s.CommandKey).Distinct(StringComparer.Ordinal).Count(), "one key per command");
        Assert.AreEqual(0, KeyboardShortcuts.Conflicts().Count, string.Join("; ", KeyboardShortcuts.Conflicts().Select(c => $"{c.First.CommandKey} = {c.Second.CommandKey} ({c.First.Gesture})")));

        // Gestures read the way a person reads them.
        Assert.AreEqual("Ctrl+K", KeyboardShortcuts.Gesture(Key.K, ModifierKeys.Control)); Assert.AreEqual("Alt+←", KeyboardShortcuts.Gesture(Key.Left, ModifierKeys.Alt));
        Assert.AreEqual("F5", KeyboardShortcuts.Gesture(Key.F5, ModifierKeys.None)); Assert.AreEqual("Esc", KeyboardShortcuts.Gesture(Key.Escape, ModifierKeys.None)); Assert.AreEqual("Ctrl+Shift+S", KeyboardShortcuts.Gesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.AreEqual("Geri (Alt+←)", KeyboardShortcuts.Hint("Geri", "back")); Assert.AreEqual("Kaydet", KeyboardShortcuts.Hint("Kaydet", "no-such-command"), "an action without a shortcut gets no hint");

        // The real commands are there, and nothing the shell does not have.
        foreach (var key in new[] { "global-search", "navigate-dashboard", "navigate-products", "toggle-sidebar", "back", "refresh-products", "product-inspect", "shortcut-reference" }) Assert.IsNotNull(KeyboardShortcuts.Find(key), key);
        Assert.IsNull(KeyboardShortcuts.Match(Key.Delete, ModifierKeys.None, ShortcutScope.Shell), "no shortcut deletes anything");
        Assert.IsNull(KeyboardShortcuts.Match(Key.Delete, ModifierKeys.None, ShortcutScope.Products));
        Assert.IsFalse(all.Any(s => s.Key == Key.Delete || (s.Modifiers == ModifierKeys.Control && s.Key == Key.S)), "nothing destructive, nothing that writes");

        // Matching: the shell scope, the products scope, and a gesture that means different things in different scopes.
        Assert.AreEqual("global-search", KeyboardShortcuts.Match(Key.K, ModifierKeys.Control, ShortcutScope.Shell)!.CommandKey);
        Assert.AreEqual("back", KeyboardShortcuts.Match(Key.Left, ModifierKeys.Alt, ShortcutScope.Shell)!.CommandKey);
        Assert.AreEqual("navigate-dashboard", KeyboardShortcuts.Match(Key.NumPad1, ModifierKeys.Control, ShortcutScope.Shell)!.CommandKey, "the number pad counts");
        Assert.AreEqual("product-inspect", KeyboardShortcuts.Match(Key.I, ModifierKeys.Control, ShortcutScope.Products)!.CommandKey);
        Assert.IsNull(KeyboardShortcuts.Match(Key.I, ModifierKeys.Control, ShortcutScope.Shell), "a products shortcut is not a shell shortcut");
        Assert.AreEqual("shortcut-reference", KeyboardShortcuts.Match(Key.F1, ModifierKeys.None, ShortcutScope.Shell)!.CommandKey);

        // The reference filters with the Turkish-safe fold, on the label and on the gesture; an empty query lists everything.
        Assert.AreEqual(all.Count, KeyboardShortcuts.Filter("").Count); Assert.AreEqual(all.Count, KeyboardShortcuts.Filter(null).Count);
        CollectionAssert.AreEquivalent(all.Where(s => s.Label.Contains("ürün", StringComparison.CurrentCultureIgnoreCase) || s.CommandKey.Contains("product")).Select(s => s.CommandKey).ToList(), KeyboardShortcuts.Filter("ÜRÜN").Select(s => s.CommandKey).ToList());
        Assert.AreEqual("toggle-sidebar", KeyboardShortcuts.Filter("ctrl+b").Single().CommandKey);
        Assert.IsTrue(KeyboardShortcuts.Filter("KISAYOL").Any(s => s.CommandKey == "shortcut-reference"), "an ASCII I finds a dotless ı");
        Assert.AreEqual(0, KeyboardShortcuts.Filter("yoktur böyle bir şey").Count);
    }
}
