using System;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #815 (DESIGN: Status tooltip content standard). Every status surface composes its tooltip from one template:
// status, reason, last change, source, next action -- empty fields hidden, long reasons cut to a sentence, raw
// payloads and secrets never shown.
[TestClass]
public sealed class StatusTooltipTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void PendingErrorStaleAndUnsupportedEachGetTheirOwnNextStep()
    {
        var texts = new[] { "pending", "error", "stale", "unsupported" }
            .Select(key => StatusTooltip.Compose(new StatusTooltipContent(key.ToUpperInvariant(), "Sebep", Now.AddMinutes(-5), "etsy / shop-a", StatusTooltip.NextActionFor(key)), Now))
            .ToArray();

        foreach (var text in texts)
        {
            StringAssert.StartsWith(text, "Durum: ");
            StringAssert.Contains(text, "\nNeden: Sebep");
            StringAssert.Contains(text, "\nKaynak: etsy / shop-a");
            StringAssert.Contains(text, "\nSonraki adım: ");
        }
        Assert.AreEqual(4, texts.Select(t => t[(t.IndexOf("Sonraki adım:", StringComparison.Ordinal))..]).Distinct().Count(), "Four states, four different next steps.");
        StringAssert.Contains(texts[3], "desteklemiyor", "Unsupported says so instead of suggesting a retry that cannot work.");
        Assert.AreEqual("", StatusTooltip.NextActionFor("normal"), "A normal row has no next step to suggest.");
    }

    [TestMethod]
    public void EmptyFieldsAreHiddenNotPrintedAsPlaceholders()
    {
        var text = StatusTooltip.Compose(new StatusTooltipContent("Bekliyor"), Now);

        Assert.AreEqual("Durum: Bekliyor", text, "Only what is known is printed; no dash lines for the rest.");
        Assert.IsFalse(text.Contains("Neden") || text.Contains("Kaynak") || text.Contains("Son değişiklik") || text.Contains("Sonraki adım"));
        Assert.AreEqual("Durum: bilinmiyor", StatusTooltip.Compose(new StatusTooltipContent("   "), Now), "An empty status is still a line, and says it is unknown.");
    }

    [TestMethod]
    public void ALongReasonBecomesOneSentence()
    {
        var reason = string.Join("\r\n", Enumerable.Repeat("Bağlantı zaman aşımına uğradı, yeniden denendi ve yine başarısız oldu.", 20));

        var text = StatusTooltip.Compose(new StatusTooltipContent("Hatalı", reason), Now);
        var reasonLine = text.Split('\n').Single(l => l.StartsWith("Neden: ", StringComparison.Ordinal));

        Assert.IsTrue(reasonLine.Length <= "Neden: ".Length + StatusTooltip.MaxReasonLength, $"Reason line is {reasonLine.Length} chars.");
        StringAssert.EndsWith(reasonLine, "…");
        Assert.IsFalse(reasonLine.Contains('\r'), "Newlines inside the reason collapse; the tooltip's own lines are the only line breaks.");
        Assert.IsTrue(text.Length <= StatusTooltip.MaxLength);
    }

    [TestMethod]
    public void RawPayloadsAndSecretsNeverReachTheTooltip()
    {
        foreach (var raw in new[]
        {
            "{\"error\":\"invalid_grant\",\"error_description\":\"token expired\"}",
            "[{\"code\":401,\"message\":\"nope\"}]",
            "<?xml version=\"1.0\"?><error><code>500</code></error>",
            "<Fault><Reason>boom</Reason></Fault>",
            "System.InvalidOperationException: boom\n   at TrMarketplaceHubDesktop.EtsyConnector.Send(HttpRequestMessage r)\n   at TrMarketplaceHubDesktop.MainWindow.Publish()",
        })
        {
            Assert.IsTrue(StatusTooltip.LooksLikeRawPayload(raw), raw);
            var text = StatusTooltip.Compose(new StatusTooltipContent("Hatalı", raw), Now);
            StringAssert.Contains(text, StatusTooltip.RawPayloadHidden);
            Assert.IsFalse(text.Contains("invalid_grant") || text.Contains("<code>") || text.Contains("   at "), $"Raw content leaked: {text}");
        }
        Assert.IsFalse(StatusTooltip.LooksLikeRawPayload("Etsy: 401 yetkisiz; token yenilenmeli."), "A sentence that mentions a token is still a sentence.");

        var secret = StatusTooltip.Compose(new StatusTooltipContent("Hatalı", "Authorization: Bearer abc.def.ghi reddedildi; müşteri ali@example.com", Now, "C:\\Users\\serif\\feed.xml", "access_token=XYZ ile yeniden deneyin"), Now);
        foreach (var forbidden in new[] { "abc.def.ghi", "ali@example.com", "\\serif\\", "XYZ" })
            Assert.IsFalse(secret.Contains(forbidden, StringComparison.Ordinal), $"'{forbidden}' reached the tooltip: {secret}");
    }

    [TestMethod]
    public void LastChangeIsRelativeAndAbsoluteInTheOperatorsLocale()
    {
        var at = Now.AddMinutes(-5);
        var text = StatusTooltip.Compose(new StatusTooltipContent("Bekliyor", LastChangeUtc: at), Now);

        StringAssert.Contains(text, "Son değişiklik: 5 dk önce (" + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ")");
        Assert.AreEqual("az önce", StatusTooltip.Relative(Now.AddSeconds(-20), Now));
        Assert.AreEqual("3 sa önce", StatusTooltip.Relative(Now.AddHours(-3), Now));
        Assert.AreEqual("2 gün önce", StatusTooltip.Relative(Now.AddDays(-2), Now));
        Assert.AreEqual("gelecekte", StatusTooltip.Relative(Now.AddMinutes(1), Now), "A clock skew is reported, not turned into a negative number.");
    }

    [TestMethod]
    public void TheProductRowTooltipGoesThroughTheSharedTemplate()
    {
        var product = new CatalogProduct { Sku = "A", Name = "Kupa", Stock = 3, Active = false, UpdatedUtc = Now.AddHours(-2) };
        var state = ProductRowState.Classify(product, Now);

        var text = ProductRowState.Tooltip(state, product);

        StringAssert.StartsWith(text, "Durum: " + state.Badge, "The badge word is the status line, so the tooltip and the badge cannot disagree.");
        StringAssert.Contains(text, "\nNeden: ");
        StringAssert.Contains(text, "Stok: " + 3.ToString(CultureInfo.CurrentCulture));
        StringAssert.Contains(text, "\nSonraki adım: ", "Every non-normal row tells the operator what to do next.");
        Assert.IsTrue(text.Length <= StatusTooltip.MaxLength);
    }
}
