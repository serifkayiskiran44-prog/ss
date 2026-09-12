using System;
using System.Linq;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #804 (DESIGN: Product media gallery keyboard navigation). Arrow / Home / End / Enter over a thumbnail strip,
// and a summary of the selected item. The rules live here rather than in key handlers so the edges -- one
// image, an empty gallery, the ends of the strip -- are decided once. The summary is host-only: a supplier
// image URL carries a signature, and this line is the thing an operator screenshots.
[TestClass]
public sealed class MediaGalleryNavigationTests
{
    static ProductMediaRecord Record(string url, MediaStatus status = MediaStatus.Ready, bool primary = false, int order = 0)
        => new() { ProductId = "p1", Url = url, NormalizedUrl = url, Status = status, IsPrimary = primary, SortOrder = order, Source = "xml" };

    [TestMethod]
    public void ArrowsMoveOneStepAndStopAtTheEndsRatherThanWrappingAround()
    {
        Assert.AreEqual(1, MediaGalleryNavigation.Next(0, 50, Key.Right));
        Assert.AreEqual(1, MediaGalleryNavigation.Next(0, 50, Key.Down));
        Assert.AreEqual(0, MediaGalleryNavigation.Next(1, 50, Key.Left));
        Assert.AreEqual(0, MediaGalleryNavigation.Next(1, 50, Key.Up));

        Assert.AreEqual(0, MediaGalleryNavigation.Next(0, 50, Key.Left), "At the first item, Left holds still: wrapping to the last image surprises an operator scanning a strip.");
        Assert.AreEqual(49, MediaGalleryNavigation.Next(49, 50, Key.Right), "...and the same at the far end.");

        Assert.AreEqual(0, MediaGalleryNavigation.Next(37, 50, Key.Home));
        Assert.AreEqual(49, MediaGalleryNavigation.Next(37, 50, Key.End));
        Assert.AreEqual(37, MediaGalleryNavigation.Next(37, 50, Key.A), "A key the gallery does not own leaves the selection alone.");
    }

    [TestMethod]
    public void AGalleryWithOneOrNoImagesHasNowhereToGoAndSaysSoWithoutThrowing()
    {
        Assert.AreEqual(0, MediaGalleryNavigation.Next(0, 1, Key.Right));
        Assert.AreEqual(0, MediaGalleryNavigation.Next(0, 1, Key.End));
        Assert.AreEqual(-1, MediaGalleryNavigation.Next(-1, 0, Key.Right), "An empty gallery keeps 'nothing selected'.");
        Assert.AreEqual(-1, MediaGalleryNavigation.Next(-1, 0, Key.Home));
        Assert.AreEqual(0, MediaGalleryNavigation.Next(-1, 5, Key.Right), "With nothing selected yet, the first key press picks the first image.");
        Assert.AreEqual(0, MediaGalleryNavigation.Next(99, 5, Key.Right), "An index left over from a longer gallery is clamped, not trusted.");
    }

    [TestMethod]
    public void EnterOpensTheSelectedImageOnlyWhenThereIsOne()
    {
        Assert.IsTrue(MediaGalleryNavigation.IsActivation(Key.Enter));
        Assert.IsTrue(MediaGalleryNavigation.IsActivation(Key.Space));
        Assert.IsFalse(MediaGalleryNavigation.IsActivation(Key.Right));
    }

    [TestMethod]
    public void TheSelectedMediaSummaryNamesPositionStateAndHostAndNeverTheUrl()
    {
        var records = new[]
        {
            Record("https://cdn.example/a.jpg?sig=abc123secret", primary: true),
            Record("https://user:pw@cdn.example/b.jpg", MediaStatus.NotFound),
            Record(""),
        };

        var first = MediaGalleryNavigation.Summary(records, 0);
        StringAssert.Contains(first, "1 / 3");
        StringAssert.Contains(first, "cdn.example");
        StringAssert.Contains(first, "Ana görsel");
        Assert.IsFalse(first.Contains("abc123secret", StringComparison.Ordinal), first);
        Assert.IsFalse(first.Contains("/a.jpg", StringComparison.Ordinal), first);

        var broken = MediaGalleryNavigation.Summary(records, 1);
        StringAssert.Contains(broken, "2 / 3");
        StringAssert.Contains(broken, "Görsel açılamadı", "The summary reuses the shared media state wording (#797).");
        Assert.IsFalse(broken.Contains("pw@", StringComparison.Ordinal), broken);

        Assert.AreEqual("Seçili görsel yok", MediaGalleryNavigation.Summary(records, -1));
        Assert.AreEqual("Seçili görsel yok", MediaGalleryNavigation.Summary(Array.Empty<ProductMediaRecord>(), 0));
        Assert.IsTrue(MediaGalleryNavigation.Summary(records, 2).Length > 0, "A record with no URL still summarizes rather than blanking the line.");
    }

    [TestMethod]
    public void CopyingTheSourceIsADeliberateActAndTheAccidentalPathIsRedacted()
    {
        const string url = "https://cdn.example/a.jpg?sig=abc123secret";

        // What a bulk/implicit copy would produce goes through the redactor...
        Assert.AreNotEqual(url, ClipboardRedaction.PrepareForCopy("Url", url));
        // ...while the operator's explicit "copy this image's address" action is allowed to hand over the real thing.
        Assert.AreEqual(url, MediaGalleryNavigation.ExplicitCopyValue(Record(url)));
        Assert.AreEqual("", MediaGalleryNavigation.ExplicitCopyValue(null));
    }
}
