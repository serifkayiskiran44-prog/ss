using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #797 (DESIGN: Product card media fallback states). Loading, missing, broken and cached have to be four
// distinguishable states sharing one presenter, and the box must reserve the same space in all of them --
// a placeholder that is a different size from the image is exactly what makes a card jump when the picture
// arrives. The remote URL is not display material: a supplier link can carry a token in its query string.
[TestClass]
public sealed class ProductMediaPresentationTests
{
    [TestMethod]
    public void TheFourStatesComeFromTheRecordAndTheLoadOutcomeRatherThanFromGuesswork()
    {
        Assert.AreEqual(ProductMediaPresentation.Missing, ProductMediaPresentation.Classify(null, loading: false, loaded: false, fromCache: false).Key, "No record at all is 'missing', not 'broken'.");
        Assert.AreEqual(ProductMediaPresentation.Missing, ProductMediaPresentation.Classify(Record("", MediaStatus.Pending), false, false, false).Key, "A record with no URL is nothing to load.");
        Assert.AreEqual(ProductMediaPresentation.Loading, ProductMediaPresentation.Classify(Record("https://cdn.example/a.jpg", MediaStatus.Pending), loading: true, loaded: false, fromCache: false).Key);
        Assert.AreEqual(ProductMediaPresentation.Ready, ProductMediaPresentation.Classify(Record("https://cdn.example/a.jpg", MediaStatus.Ready), false, loaded: true, fromCache: false).Key);
        Assert.AreEqual(ProductMediaPresentation.Cached, ProductMediaPresentation.Classify(Record("https://cdn.example/a.jpg", MediaStatus.Ready), false, loaded: true, fromCache: true).Key, "A cache hit is worth distinguishing: it tells the operator the picture is not a fresh fetch.");

        foreach (var failure in new[] { MediaStatus.NotFound, MediaStatus.Timeout, MediaStatus.InvalidUrl, MediaStatus.TooLarge, MediaStatus.UnsupportedFormat, MediaStatus.Error, MediaStatus.RateLimited })
            Assert.AreEqual(ProductMediaPresentation.Broken, ProductMediaPresentation.Classify(Record("https://cdn.example/a.jpg", failure), false, false, false).Key, failure.ToString());

        // A record the store believes is fine but that would not decode is still broken to the operator.
        Assert.AreEqual(ProductMediaPresentation.Broken, ProductMediaPresentation.Classify(Record("https://cdn.example/a.jpg", MediaStatus.Ready), loading: false, loaded: false, fromCache: false, failed: true).Key);
    }

    [TestMethod]
    public void EveryStateReservesTheSameBoxSoTheCardCannotJumpWhenThePictureArrives()
    {
        var keys = new[] { ProductMediaPresentation.Loading, ProductMediaPresentation.Missing, ProductMediaPresentation.Broken, ProductMediaPresentation.Ready, ProductMediaPresentation.Cached };
        var sizes = keys.Select(k => ProductMediaPresentation.Describe(k).BoxSize).Distinct().ToArray();

        Assert.AreEqual(1, sizes.Length, "All states must reserve identical space; differing sizes are the layout shift the issue forbids.");
        Assert.IsTrue(sizes[0] > 0);
        foreach (var key in keys)
        {
            var info = ProductMediaPresentation.Describe(key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Label), $"{key} needs a word, not just a colour or an empty box.");
            if (key is ProductMediaPresentation.Ready or ProductMediaPresentation.Cached) continue;
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Glyph), $"{key} needs a placeholder glyph.");
        }
        Assert.AreEqual(ProductMediaPresentation.Describe(ProductMediaPresentation.Loading).Label != ProductMediaPresentation.Describe(ProductMediaPresentation.Missing).Label, true, "Loading and missing must never read the same; that is the whole point of the distinction.");
        Assert.AreNotEqual(ProductMediaPresentation.Describe(ProductMediaPresentation.Missing).Label, ProductMediaPresentation.Describe(ProductMediaPresentation.Broken).Label);
    }

    [TestMethod]
    public void TheRemoteUrlIsReducedToItsHostBeforeItIsAllowedNearTheUiOrALog()
    {
        Assert.AreEqual("cdn.example", ProductMediaPresentation.SafeSourceLabel("https://cdn.example/products/a.jpg?sig=abc123secret&token=zzz"));
        Assert.AreEqual("cdn.example", ProductMediaPresentation.SafeSourceLabel("https://user:pass@cdn.example/a.jpg"), "Credentials embedded in a URL must not survive into a label.");
        Assert.AreEqual("yerel dosya", ProductMediaPresentation.SafeSourceLabel("file:///C:/Users/serif/pictures/a.jpg"), "A local path names a person's folder; the label says only that it is local.");
        Assert.AreEqual("—", ProductMediaPresentation.SafeSourceLabel(""));
        Assert.AreEqual("—", ProductMediaPresentation.SafeSourceLabel("not a url at all"));

        foreach (var label in new[] { ProductMediaPresentation.SafeSourceLabel("https://cdn.example/a.jpg?sig=abc123secret"), ProductMediaPresentation.SafeSourceLabel("https://user:pass@cdn.example/a.jpg") })
        {
            Assert.IsFalse(label.Contains("abc123secret", StringComparison.Ordinal), label);
            Assert.IsFalse(label.Contains("pass", StringComparison.Ordinal), label);
            Assert.IsFalse(label.Contains("/", StringComparison.Ordinal), label);
        }
    }

    [TestMethod]
    public void AFailureMessageIsSummarizedForTheOperatorWithoutTheUrlOrTheRawException()
    {
        var text = ProductMediaPresentation.FailureText(Record("https://cdn.example/a.jpg?token=zzz999", MediaStatus.NotFound), "404 for https://cdn.example/a.jpg?token=zzz999");

        StringAssert.Contains(text, "bulunamadı", "The operator is told what went wrong in their own words.");
        StringAssert.Contains(text, "cdn.example", "...and which host it was, which is the part that helps.");
        Assert.IsFalse(text.Contains("zzz999", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("?token", StringComparison.Ordinal), text);
        Assert.IsTrue(text.Length <= 200, "A failure line stays a line.");
    }

    static ProductMediaRecord Record(string url, MediaStatus status) => new() { ProductId = "p1", Url = url, NormalizedUrl = url, Status = status };
}
