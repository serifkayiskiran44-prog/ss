using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #862 (DESIGN SYSTEM: Contrast audit for semantic states). The audit is WCAG's relative luminance and contrast
// ratio over a catalogue of the pairs the main screens draw -- text on its surface, a state's accent as text, as
// a border and as an icon, the button, the navigation rail, the selected row, the focus ring, the toasts -- read
// from the tokens and the severity table, so a colour that drifts is caught here. Every text pair reaches 4.5:1,
// every border or icon 3:1; a disabled control is measured and reported exempt; high contrast uses the system's
// pairs. No state relies on colour alone: every severity carries a glyph and a word, and a selected row a rule.
[TestClass]
public sealed class ContrastAuditTests
{
    [TestMethod]
    public void TheMathMatchesWcagAndEveryCataloguedPairPasses()
    {
        RunSta(() =>
        {
            Assert.AreEqual(21.0, ContrastAudit.Ratio(Colors.Black, Colors.White), 0.01); Assert.AreEqual(1.0, ContrastAudit.Ratio(Colors.White, Colors.White), 0.001);
            Assert.AreEqual(4.54, ContrastAudit.Ratio(Color.FromRgb(118, 118, 118), Colors.White), 0.02, "#767676 on white is the classic 4.5 boundary.");
            Assert.AreEqual(ContrastAudit.Ratio(Colors.White, Colors.Black), ContrastAudit.Ratio(Colors.Black, Colors.White), 0.0001, "Order does not matter.");
            Assert.AreEqual(Color.FromRgb(128, 128, 128), ContrastAudit.Blend(Colors.Black, 0.5, Colors.White), "A half-transparent black over white is mid grey.");
            Assert.AreEqual(4.5, ContrastAudit.Required(ContrastKind.Text)); Assert.AreEqual(3.0, ContrastAudit.Required(ContrastKind.LargeText)); Assert.AreEqual(3.0, ContrastAudit.Required(ContrastKind.NonText)); Assert.AreEqual(0.0, ContrastAudit.Required(ContrastKind.Disabled));

            // The tokens the audit reads exist, and the pairs that used to fail are fixed at the token.
            DesignTokens.Verify(DesignTokens.Resources);
            foreach (var key in new[] { "TextPrimaryColor", "TextMutedColor", "TextSecondaryColor", "WarningTextColor", "AccentColor", "AccentForegroundColor", "PageBackgroundColor", "SurfaceColor", "RailBackgroundColor", "RailForegroundColor", "RailSelectedColor", "RailSubtitleColor", "SelectedRowColor", "SelectedRowForegroundColor", "SelectedRowInactiveColor" })
                Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == key && r.Type == typeof(Color)), key);
            Assert.IsTrue(ContrastAudit.Ratio(DesignTokens.TextMutedColor, DesignTokens.SurfaceColor) >= 4.5, "Muted text (hints, help, captions) reads on white.");
            Assert.IsTrue(ContrastAudit.Ratio(DesignTokens.WarningTextColor, DesignTokens.SurfaceColor) >= 4.5, "A warning as text reads on white.");
            Assert.IsTrue(ContrastAudit.Ratio(DesignTokens.SelectedRowForegroundColor, DesignTokens.SelectedRowColor) >= 4.5, "A selected row's text reads on its highlight.");
            Assert.IsTrue(ContrastAudit.Ratio(DesignTokens.TextPrimaryColor, DesignTokens.SelectedRowInactiveColor) >= 4.5, "An unfocused selection keeps readable text.");
            Assert.IsTrue(ContrastAudit.Ratio(DesignTokens.AccentForegroundColor, DesignTokens.AccentColor) >= 4.5, "The button's label reads on the button.");

            // The catalogue: the main screens' pairs, every one passing its threshold; disabled measured and exempt.
            var findings = ContrastAudit.Audit(highContrast: false);
            Assert.IsTrue(findings.Count >= 30, $"{findings.Count} pairs audited.");
            var failing = findings.Where(f => !f.Passes).Select(f => $"{f.Name} {f.Ratio:0.00} < {f.Required}").ToList();
            Assert.AreEqual(0, failing.Count, string.Join("; ", failing));
            foreach (var level in new[] { SeverityLevel.Blocking, SeverityLevel.Warning, SeverityLevel.Success, SeverityLevel.Info })
            {
                var word = SeverityStyle.For(level, false).Word;
                Assert.IsTrue(findings.Any(f => f.Name.StartsWith(word, StringComparison.Ordinal) && f.Kind == ContrastKind.Text), $"{word}: text audited");
                Assert.IsTrue(findings.Any(f => f.Name.StartsWith(word, StringComparison.Ordinal) && f.Kind == ContrastKind.NonText), $"{word}: border or icon audited");
            }
            var disabled = findings.Where(f => f.Kind == ContrastKind.Disabled).ToList();
            Assert.IsTrue(disabled.Count > 0 && disabled.All(f => f.Passes && f.Required == 0 && f.Ratio > 1), "Disabled pairs are measured and reported exempt, never hidden.");
            Assert.IsTrue(findings.Any(f => f.Name.Contains("Seçili satır", StringComparison.Ordinal)) && findings.Any(f => f.Name.Contains("Odak", StringComparison.Ordinal)) && findings.Any(f => f.Name.Contains("Menü", StringComparison.Ordinal)));

            // High contrast: the system's pairs, and the severities' system accents, all audited under the same rules.
            var hc = ContrastAudit.Audit(highContrast: true);
            Assert.IsTrue(hc.Count >= 8); Assert.IsTrue(hc.All(f => f.Passes), string.Join("; ", hc.Where(f => !f.Passes).Select(f => f.Name)));
            Assert.IsTrue(hc.Where(f => f.Kind == ContrastKind.System).All(f => f.Ratio > 1 && f.Required == 0), "The system's pairs are measured and reported, never failed on the user's palette.");
            Assert.IsTrue(hc.Any(f => f.Kind == ContrastKind.NonText), "The severities' system accents are audited as emphasis.");

            // Never colour alone: every severity has a glyph and a word; the selected row carries a rule, not only a fill.
            foreach (var level in new[] { SeverityLevel.Blocking, SeverityLevel.Warning, SeverityLevel.Success, SeverityLevel.Info })
            { var style = SeverityStyle.For(level, false); Assert.IsTrue(style.Glyph.Length > 0 && style.Word.Length > 0, level.ToString()); }
            Assert.IsTrue(DesignTokens.SelectedRowRuleThickness >= 2, "A selected row is also marked by a rule.");
        });
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
