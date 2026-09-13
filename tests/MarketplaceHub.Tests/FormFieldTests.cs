using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #819 (DESIGN: Form label and help-text alignment). One row shape for every form: label above, required spoken
// as a word, help under the input, a validation slot under that; DIP constants for spacing; labels wrap; help
// text never carries a specimen secret.
[TestClass]
public sealed class FormFieldTests
{
    [TestMethod]
    public void RequiredIsAMarkerForTheEyeAndAWordForTheEar()
    {
        var required = new FormFieldSpec("Ürün adı", Required: true);
        var optional = new FormFieldSpec("Açıklama");

        Assert.AreEqual("Ürün adı *", FormField.LabelText(required));
        Assert.AreEqual("Ürün adı, zorunlu", FormField.AccessibleName(required), "A screen reader hears the word, not an asterisk.");
        Assert.AreEqual("Açıklama", FormField.LabelText(optional));
        Assert.AreEqual("Açıklama", FormField.AccessibleName(optional));
        Assert.IsTrue(FormField.LabelText(required).StartsWith("Ürün adı", StringComparison.Ordinal), "The marker trails so labels still scan by their first letter.");
    }

    [TestMethod]
    public void HelpTextIsCopyNotASpecimen()
    {
        Assert.AreEqual("Üç harfli kod, örneğin TRY veya USD.", FormField.SafeHelp("Üç harfli kod,  örneğin\nTRY veya USD."));
        foreach (var specimen in new[] { "Örn: api_key=abc123", "token: eyJhbGci", "Bearer abc.def", "Parola: gizli123", "client_secret=xyz" })
            Assert.AreEqual("", FormField.SafeHelp(specimen), $"'{specimen}' is a credential shown as an example; help refuses it.");
        Assert.IsFalse(FormField.SafeHelp("Adresi ali@example.com biçiminde girin.").Contains("ali@example.com"), "PII in help is redacted.");
        Assert.AreEqual("", FormField.SafeHelp(null));
    }

    [TestMethod]
    public void TheBuiltRowHasLabelInputHelpAndAnEmptyValidationSlotInThatOrderAndTheInputIsLabeled()
    {
        Run(() =>
        {
            var box = new TextBox();
            var row = FormField.Build(new FormFieldSpec("Satış para birimi", Required: true, Help: "Üç harfli kod, örneğin TRY."), box);

            CollectionAssert.AreEqual(new[] { "TextBlock", "TextBox", "TextBlock", "TextBlock" }, row.Root.Children.Cast<UIElement>().Select(c => c.GetType().Name).ToArray());
            Assert.AreSame(row.LabelText, AutomationProperties.GetLabeledBy(box), "The input is labeled by its own label for assistive tech.");
            Assert.AreEqual("Satış para birimi, zorunlu", AutomationProperties.GetName(box));
            Assert.AreEqual("Üç harfli kod, örneğin TRY.", row.HelpText.Text);
            Assert.AreEqual("Üç harfli kod, örneğin TRY.", AutomationProperties.GetHelpText(box), "Help text is spoken with the field.");
            Assert.AreEqual(Visibility.Collapsed, row.ValidationText.Visibility, "No rule has spoken yet.");
            Assert.AreEqual(TextWrapping.Wrap, row.LabelText.TextWrapping);
            Assert.AreEqual(new Thickness(0, 0, 0, FormField.RowGap), row.Root.Margin, "Row rhythm is a DIP constant.");

            var plain = FormField.Build(new FormFieldSpec("Açıklama"), new TextBox());
            Assert.AreEqual(Visibility.Collapsed, plain.HelpText.Visibility, "No help, no empty help line.");
        });
    }

    [TestMethod]
    public void ALongTurkishLabelWrapsAndGrowsTheRowInsteadOfBeingClipped()
    {
        Run(() =>
        {
            var row = FormField.Build(new FormFieldSpec("Tedarikçi XML kaynağındaki ürün açıklaması alanının hedef eşlemesi ve varsayılan değeri", Required: true), new TextBox());

            row.Root.Measure(new Size(700, double.PositiveInfinity)); var wide = row.LabelText.DesiredSize.Height;
            row.Root.Measure(new Size(220, double.PositiveInfinity)); var narrow = row.LabelText.DesiredSize.Height;

            Assert.IsTrue(narrow > wide * 1.5, $"At 220 DIP the label must wrap to more lines (narrow {narrow} vs wide {wide}).");
            Assert.IsTrue(row.LabelText.DesiredSize.Width <= 220, "The label never asks for more width than the row has.");
        });
    }

    [TestMethod]
    public void AValidationMessageAppearsUnderTheInputInTheBlockingStyleAndClears()
    {
        Run(() =>
        {
            var box = new TextBox();
            var row = FormField.Build(new FormFieldSpec("Ürün adı", Required: true, Help: "Mağazada görünen ad."), box);

            row.SetValidation("Ürün adı zorunlu.");
            Assert.AreEqual(Visibility.Visible, row.ValidationText.Visibility);
            StringAssert.StartsWith(row.ValidationText.Text, SeverityStyle.For(SeverityLevel.Blocking, false).Glyph, "The message carries the blocking glyph, not only its colour.");
            StringAssert.Contains(AutomationProperties.GetHelpText(box), "Ürün adı zorunlu.", "The field itself speaks its error.");

            row.SetValidation("Authorization: Bearer abc.def reddedildi");
            Assert.IsFalse(row.ValidationText.Text.Contains("abc.def"), "Validation text is redacted too.");

            row.SetValidation("");
            Assert.AreEqual(Visibility.Collapsed, row.ValidationText.Visibility);
            Assert.AreEqual("Mağazada görünen ad.", AutomationProperties.GetHelpText(box), "Clearing the error restores the plain help.");
        });
    }

    static void Run(Action test)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
