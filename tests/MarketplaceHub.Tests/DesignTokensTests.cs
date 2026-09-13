using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #857 (DESIGN SYSTEM: Resource token consolidation). One resource dictionary carries the semantic spacing, radius,
// border and control-height tokens; the app merges it, the window merges it, and the code reads it through one
// accessor. The values are the DIP numbers the screens already used, so nothing moves on screen. A token that is
// missing or of the wrong type is a startup failure that names the key, never a silent default.
[TestClass]
public sealed class DesignTokensTests
{
    [TestMethod]
    public void TheDictionaryCarriesEveryTokenWithItsTypeAndTheValuesTheScreensAlreadyUsed()
    {
        RunSta(() =>
        {
            var tokens = DesignTokens.Resources;
            DesignTokens.Verify(tokens);
            foreach (var (key, type) in DesignTokens.Required) { Assert.IsTrue(tokens.Contains(key), key); Assert.IsInstanceOfType(tokens[key], type, key); }
            Assert.IsTrue(DesignTokens.Required.Count >= 18); Assert.AreEqual(DesignTokens.Required.Count, DesignTokens.Required.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());

            // Visual regression by value: the same DIP numbers the styles and panels used before the tokens existed.
            Assert.AreEqual(2d, DesignTokens.SpaceHairline); Assert.AreEqual(4d, DesignTokens.SpaceInline); Assert.AreEqual(8d, DesignTokens.SpaceControl); Assert.AreEqual(12d, DesignTokens.SpaceSection); Assert.AreEqual(20d, DesignTokens.SpacePage);
            Assert.AreEqual(new Thickness(3), DesignTokens.ControlMargin); Assert.AreEqual(new Thickness(7, 5, 7, 5), DesignTokens.InputPadding); Assert.AreEqual(new Thickness(13, 8, 13, 8), DesignTokens.ButtonPadding); Assert.AreEqual(new Thickness(10, 5, 10, 5), DesignTokens.CompactButtonPadding);
            Assert.AreEqual(new Thickness(10), DesignTokens.CardPadding); Assert.AreEqual(new Thickness(8, 10, 8, 10), DesignTokens.HeaderPadding); Assert.AreEqual(new Thickness(16, 9, 16, 9), DesignTokens.TabPadding);
            Assert.AreEqual(new Thickness(14, 9, 14, 9), DesignTokens.NavigationItemPadding); Assert.AreEqual(new Thickness(8, 2, 8, 2), DesignTokens.NavigationItemMargin);
            Assert.AreEqual(30d, DesignTokens.ControlMinHeight); Assert.AreEqual(36d, DesignTokens.RowHeight);
            Assert.AreEqual(1d, DesignTokens.BorderHairline); Assert.AreEqual(2d, DesignTokens.BorderEmphasis);
            Assert.AreEqual(new CornerRadius(6), DesignTokens.CardRadius); Assert.AreEqual(new CornerRadius(7), DesignTokens.ShellRadius);
        });
    }

    [TestMethod]
    public void AMissingOrMistypedTokenIsAStartupFailureThatNamesTheKey()
    {
        RunSta(() =>
        {
            var missing = Copy(DesignTokens.Resources); missing.Remove("SpaceSection");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => DesignTokens.Verify(missing)).Message, "SpaceSection");
            var mistyped = Copy(DesignTokens.Resources); mistyped["CardRadius"] = "6";
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => DesignTokens.Verify(mistyped)).Message, "CardRadius");
            var empty = new ResourceDictionary();
            var all = Assert.ThrowsException<InvalidOperationException>(() => DesignTokens.Verify(empty));
            foreach (var (key, _) in DesignTokens.Required) StringAssert.Contains(all.Message, key);
            // The app's startup runs the same check over its merged resources, before any window exists.
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => App.VerifyDesignTokens(empty)).Message, "SpacePage");
            // A dictionary that merges the token file passes, as App.xaml's does.
            var merged = new ResourceDictionary(); merged.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            App.VerifyDesignTokens(merged);
        });
    }

    static ResourceDictionary Copy(ResourceDictionary source) { var copy = new ResourceDictionary(); foreach (var key in source.Keys) copy[key] = source[key]; return copy; }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
