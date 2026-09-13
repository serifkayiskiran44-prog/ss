using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #871 (DESIGN: Disabled command reason tooltips). A disabled command says why, in one shared way: the reason has a
// kind (a capability the channel lacks, a validation that blocks, a selection that is missing or too large, a
// store state that is not there yet, or work in progress) and a text; while disabled the control's tooltip shows
// the reason (and shows while disabled), its automation help text carries it for a screen reader, and, where the
// caller gives a reason line, the line shows it in plain view so a keyboard user who cannot focus a disabled
// control still reads it; enabled again, the tooltip and the line go back to normal. A capability reason exists
// only for an operation the real capability set lacks, and a reason never carries a secret.
[TestClass]
public sealed class CommandStateTests
{
    [TestMethod]
    public void EachKindOfReasonExplainsItselfWhileDisabledAndClearsWhenEnabled()
    {
        RunSta(() =>
        {
            var button = new Button { Content = "Uygula", ToolTip = "Seçili satırları uygular" };
            var line = CommandState.ReasonLine(highContrast: false);
            Assert.AreEqual(Visibility.Collapsed, line.Visibility, "no reason, no line");
            Assert.AreEqual("command-reason", line.Tag);

            var reasons = new[]
            {
                (DisabledReason.Selection("Önce ürün seçin."), "Seçim: Önce ürün seçin."),
                (DisabledReason.Validation("2 engelleyici bulgu var."), "Doğrulama: 2 engelleyici bulgu var."),
                (DisabledReason.StoreState("Henüz sonuç yok."), "Durum: Henüz sonuç yok."),
                (DisabledReason.Busy("Rapor çalışıyor."), "Sürüyor: Rapor çalışıyor."),
            };
            foreach (var (reason, expected) in reasons)
            {
                CommandState.Apply(button, reason, line);
                Assert.IsFalse(button.IsEnabled, expected); Assert.AreEqual("Seçili satırları uygular — " + expected, button.ToolTip, "the tooltip keeps what the command does and adds why it cannot"); Assert.IsTrue(ToolTipService.GetShowOnDisabled(button), "and it shows while disabled");
                Assert.AreEqual(expected, AutomationProperties.GetHelpText(button), "a screen reader gets the reason");
                Assert.AreSame(reason, CommandState.ReasonOf(button), "the structured reason stays readable");
                Assert.AreEqual(Visibility.Visible, line.Visibility); Assert.AreEqual(expected, line.Text, "the line shows it in plain view");
            }

            CommandState.Apply(button, null, line);
            Assert.IsTrue(button.IsEnabled); Assert.AreEqual("Seçili satırları uygular", button.ToolTip, "the ordinary tooltip is back"); Assert.AreEqual("", AutomationProperties.GetHelpText(button)); Assert.IsNull(CommandState.ReasonOf(button));
            Assert.AreEqual(Visibility.Collapsed, line.Visibility); Assert.AreEqual("", line.Text);

            // A control without an ordinary tooltip gets none back.
            var plain = new Button { Content = "Sil" };
            CommandState.Apply(plain, DisabledReason.Selection("Silmek için tek ürün seçin.")); Assert.AreEqual("Seçim: Silmek için tek ürün seçin.", plain.ToolTip);
            CommandState.Apply(plain, null); Assert.IsNull(plain.ToolTip); Assert.IsTrue(plain.IsEnabled);

            // A capability reason exists only for an operation the real capability set lacks; nothing is invented.
            var capabilities = new MarketplaceCapabilities(new HashSet<MarketplaceOperation> { MarketplaceOperation.OrdersRead });
            Assert.IsNull(DisabledReason.ForCapability(capabilities, MarketplaceOperation.OrdersRead, "Etsy"), "a supported operation has no reason");
            var missing = DisabledReason.ForCapability(capabilities, MarketplaceOperation.StockWrite, "Etsy")!;
            Assert.AreEqual(DisabledReasonKind.Capability, missing.Kind); StringAssert.StartsWith(missing.Describe(), "Desteklenmiyor:"); StringAssert.Contains(missing.Describe(), "Etsy");

            // A reason never carries a secret: a bearer value or an address in a message is masked, and the value never reaches a tooltip.
            var leaky = DisabledReason.Validation("Bağlantı reddedildi: Authorization: Bearer abc123xyz · ali@example.com");
            CommandState.Apply(plain, leaky);
            Assert.IsFalse(((string)plain.ToolTip).Contains("abc123xyz")); Assert.IsFalse(((string)plain.ToolTip).Contains("ali@example.com")); StringAssert.Contains((string)plain.ToolTip, "Bağlantı reddedildi");

            // The reason line reads as a hint; under high contrast it takes the system's disabled text colour.
            Assert.AreEqual(TextStyles.MutedColor, ((SolidColorBrush)line.Foreground).Color);
            Assert.AreSame(SystemColors.GrayTextBrush, CommandState.ReasonLine(highContrast: true).Foreground);

            // The reason line lives next to its control in a real window and wraps a long Turkish reason instead of cutting it.
            var host = new StackPanel { Width = 220 }; host.Children.Add(button); host.Children.Add(line);
            var window = new Window { Content = host, Width = 300, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                CommandState.Apply(button, DisabledReason.Validation("Bu raporu çalıştırmadan önce mağaza seçin ve tarih aralığını doğrulayın; engelleyici bulgular giderilmeli."), line); Drain(window);
                Assert.AreEqual(TextWrapping.Wrap, line.TextWrapping); Assert.IsTrue(line.ActualHeight > line.FontSize * 1.8, "a long reason wraps");
                Assert.AreEqual(0, OverflowAudit.Audit(host).Count, "nothing is cut");
            }
            finally { window.Close(); }
        });
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
