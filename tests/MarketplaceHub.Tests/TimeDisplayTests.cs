using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #866 (DESIGN: Date and time display consistency). One owner for every timestamp a person reads: the local wall
// clock in the current culture's short format, a tooltip that names the zone offset of that instant, the age, and
// the UTC instant, a dash for a time that was never recorded, the same words across DST boundaries, and a column
// wide enough for the longest locale. The stores keep UTC; the display converts, never the other way round.
[TestClass]
public sealed class TimeDisplayTests
{
    static readonly TimeZoneInfo Cet = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
    static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");

    sealed record Row(string Name, DateTime? UpdatedUtc);

    [TestMethod]
    public void LocalDisplayCarriesZoneAgeAndUtcAndSurvivesDstAndMissing()
    {
        RunSta(() =>
        {
            var culture = CultureInfo.CurrentCulture;
            var at = new DateTime(2026, 9, 13, 12, 33, 0, DateTimeKind.Utc); var now = at.AddMinutes(5);
            var local = new DateTime(2026, 9, 13, 15, 33, 0);

            // Missing: never recorded is a dash, and the tooltip says so in words; a caller may put its own word in the dash's place.
            Assert.AreEqual("—", TimeDisplay.Missing);
            Assert.AreEqual(TimeDisplay.Missing, TimeDisplay.Format((DateTime?)null)); Assert.AreEqual(TimeDisplay.Missing, TimeDisplay.Format(default(DateTime))); Assert.AreEqual(TimeDisplay.Missing, TimeDisplay.Format(DateTime.MinValue));
            Assert.AreEqual(TimeDisplay.Missing, TimeDisplay.Format((DateTimeOffset?)null)); Assert.AreEqual("yok", TimeDisplay.Format((DateTime?)null, missing: "yok"));
            Assert.AreEqual(TimeDisplay.MissingDescription, TimeDisplay.Describe(null, now)); StringAssert.Contains(TimeDisplay.MissingDescription, "yok");

            // UTC in, local out, in the current culture; an unspecified kind is UTC (the stores keep UTC); an offset value is the same instant.
            Assert.AreEqual(local.ToString("g", culture), TimeDisplay.Format(at, Turkey));
            Assert.AreEqual(local.ToString("g", culture), TimeDisplay.Format(new DateTime(2026, 9, 13, 12, 33, 0, DateTimeKind.Unspecified), Turkey));
            Assert.AreEqual(local.ToString("g", culture), TimeDisplay.Format(new DateTimeOffset(at), Turkey));
            Assert.AreEqual(local.ToString("d", culture), TimeDisplay.FormatDate(at, Turkey)); Assert.AreEqual(TimeDisplay.Missing, TimeDisplay.FormatDate(null));
            var description = TimeDisplay.Describe(at, now, Turkey);
            StringAssert.Contains(description, local.ToString("g", culture)); StringAssert.Contains(description, "UTC+03:00"); StringAssert.Contains(description, "5 dk önce"); StringAssert.Contains(description, "UTC " + at.ToString("g", culture));
            Assert.AreEqual("UTC+03:00", TimeDisplay.Offset(at, Turkey)); Assert.AreEqual("UTC-05:00", TimeDisplay.Offset(at, Bogota)); Assert.AreEqual("UTC", TimeDisplay.Offset(at, TimeZoneInfo.Utc));

            // DST, spring: the hour that does not exist never shows; the offset changes with the instant.
            var beforeSpring = new DateTime(2026, 3, 29, 0, 59, 59, DateTimeKind.Utc); var afterSpring = new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 3, 29, 1, 59, 59).ToString("g", culture), TimeDisplay.Format(beforeSpring, Cet)); Assert.AreEqual("UTC+01:00", TimeDisplay.Offset(beforeSpring, Cet));
            Assert.AreEqual(new DateTime(2026, 3, 29, 3, 0, 0).ToString("g", culture), TimeDisplay.Format(afterSpring, Cet)); Assert.AreEqual("UTC+02:00", TimeDisplay.Offset(afterSpring, Cet));
            // DST, autumn: two instants share a wall clock; the tooltip's offset tells them apart.
            var firstHalfPastTwo = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc); var secondHalfPastTwo = new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc);
            Assert.AreEqual(TimeDisplay.Format(firstHalfPastTwo, Cet), TimeDisplay.Format(secondHalfPastTwo, Cet));
            Assert.AreEqual("UTC+02:00", TimeDisplay.Offset(firstHalfPastTwo, Cet)); Assert.AreEqual("UTC+01:00", TimeDisplay.Offset(secondHalfPastTwo, Cet));
            Assert.AreNotEqual(TimeDisplay.Describe(firstHalfPastTwo, now, Cet), TimeDisplay.Describe(secondHalfPastTwo, now, Cet));

            // Cultures, a long one among them: the current culture's own short format, and each fits the shared column width.
            var typeface = new Typeface(DesignTokens.FontFamilyBody, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (var name in new[] { "tr-TR", "en-US", "de-DE", "ja-JP" })
            {
                var c = CultureInfo.GetCultureInfo(name); var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = c;
                    var text = TimeDisplay.Format(at, Turkey); Assert.AreEqual(local.ToString("g", c), text, name);
                    var width = new FormattedText(text, c, FlowDirection.LeftToRight, typeface, DesignTokens.TextBodySize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                    Assert.IsTrue(width + 16 <= TimeDisplay.ColumnWidth, $"{name} needs {width:F0} DIP plus the cell's own room inside {TimeDisplay.ColumnWidth}");
                }
                finally { CultureInfo.CurrentCulture = previous; }
            }

            // The owner's other readers: an order's sync label and a converter for bindings.
            Assert.AreEqual(TimeDisplay.Format(at), new OrderSnapshot { LastSync = at }.SyncLabel); StringAssert.Contains(new OrderSnapshot().SyncLabel, "yok");
            var converter = new TimeDisplayConverter();
            Assert.AreEqual(TimeDisplay.Format(at), converter.Convert(at, typeof(string), null, culture)); Assert.AreEqual(TimeDisplay.Format(new DateTimeOffset(at)), converter.Convert(new DateTimeOffset(at), typeof(string), null, culture));
            Assert.AreEqual(TimeDisplay.Missing, converter.Convert(null, typeof(string), null, culture)); Assert.AreEqual(TimeDisplay.Missing, converter.Convert(default(DateTime), typeof(string), null, culture)); Assert.AreEqual("", converter.Convert("not a time", typeof(string), null, culture));
        });
    }

    [TestMethod]
    public void TimeColumnsShowLocalTextWithAKeyboardTooltipAndFitTheCultureAtTheSharedWidth()
    {
        RunSta(() =>
        {
            foreach (var path in new[] { "UpdatedUtc", "LastTestUtc", "StartedUtc", "CreatedAt", "FinishedUtc" }) Assert.AreEqual(GridTextKind.Time, GridColumns.KindFor(path), path);
            Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("DateLabel")); Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("SyncLabel")); Assert.AreEqual(GridTextKind.Number, GridColumns.KindFor("Updated"));
            var column = GridColumns.Text("Güncelleme", "UpdatedUtc", TimeDisplay.ColumnWidth);
            Assert.IsInstanceOfType(((Binding)column.Binding).Converter, typeof(TimeDisplayConverter)); Assert.IsNull(((Binding)column.Binding).StringFormat);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, Setter(column.ElementStyle, TextBlock.TextTrimmingProperty), "a narrowed column ends in an ellipsis, never a clipped digit");
            Assert.AreEqual(FontNumeralAlignment.Tabular, Setter(column.ElementStyle, Typography.NumeralAlignmentProperty)); Assert.IsFalse(GridColumns.GetIsNumeric(column));

            var past = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 300, Height = 120, ItemsSource = new List<Row> { new("a", past), new("b", null) } };
            grid.Columns.Add(GridColumns.Text("Ad", "Name", 60)); grid.Columns.Add(column);
            var window = new Window { Content = grid, Width = 400, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                var first = Descendants((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)).OfType<DataGridCell>().ElementAt(1);
                var block = (TextBlock)first.Content;
                Assert.AreEqual(TimeDisplay.Format(past), block.Text, "the local wall clock in the current culture");
                Assert.IsFalse(GridColumns.IsTrimmed(block), "the shared width fits the current culture");
                var tooltip = (string)first.ToolTip; Assert.AreEqual(TimeDisplay.Describe(past, DateTime.UtcNow), tooltip); StringAssert.Contains(tooltip, TimeDisplay.Offset(past, null)); StringAssert.Contains(tooltip, "önce");
                Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(first), "the keyboard opens the tooltip on the cell");
                var second = Descendants((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1)).OfType<DataGridCell>().ElementAt(1);
                Assert.AreEqual(TimeDisplay.Missing, ((TextBlock)second.Content).Text); Assert.AreEqual(TimeDisplay.MissingDescription, (string)second.ToolTip);
            }
            finally { window.Close(); }
        });
    }

    static object Setter(Style style, DependencyProperty property) => style.Setters.OfType<Setter>().Single(s => s.Property == property).Value;

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
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
