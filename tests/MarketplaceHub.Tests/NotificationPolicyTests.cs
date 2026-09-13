using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #814 (DESIGN: Global toast queue policy). One queue decides how long a toast lives, what a duplicate does,
// how many can stack, what happens when twenty arrive at once, and that an error waits for a person. The text
// that reaches the screen has been through the central sanitizer: no secret, no PII, no newline, no novel.
[TestClass]
public sealed class NotificationPolicyTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
    static NotificationRequest Info(string text) => new(NotificationSeverity.Info, text);

    [TestMethod]
    public void ABurstOfTwentyShowsThreeAndQueuesTheRestInOrder()
    {
        var queue = new NotificationQueue();
        for (var i = 1; i <= 20; i++) queue.Publish(Info($"Olay {i}"), T0.AddMilliseconds(i));

        Assert.AreEqual(NotificationQueue.MaxVisible, queue.Visible.Count, "Twenty toasts on screen is a wall, not a notification.");
        CollectionAssert.AreEqual(new[] { "Olay 1", "Olay 2", "Olay 3" }, queue.Visible.Select(t => t.Text).ToArray(), "The first to arrive are the first shown.");
        Assert.AreEqual(17, queue.PendingCount);
        Assert.AreEqual(0, queue.DroppedCount, "Nothing was lost; the rest are waiting their turn.");
    }

    [TestMethod]
    public void ADuplicateWithinTheWindowCollapsesIntoOneToastWithACount()
    {
        var queue = new NotificationQueue();
        var first = queue.Publish(new(NotificationSeverity.Warning, "Sync başarısız · etsy/price"), T0);
        var again = queue.Publish(new(NotificationSeverity.Warning, "Sync başarısız · etsy/price"), T0.AddSeconds(5));
        var third = queue.Publish(new(NotificationSeverity.Warning, "Sync başarısız · etsy/price"), T0.AddSeconds(9));

        Assert.AreEqual(1, queue.Visible.Count, "The same news three times is one toast, not three.");
        Assert.AreEqual(first.Id, again.Id);
        Assert.AreEqual(3, third.Count);
        Assert.AreEqual(T0.AddSeconds(9), third.ShownUtc, "A repeat refreshes the toast rather than letting it expire mid-burst.");
        Assert.IsTrue(third.ExpiresUtc > first.ExpiresUtc);

        queue.Publish(new(NotificationSeverity.Error, "Sync başarısız · etsy/price"), T0.AddSeconds(10));
        Assert.AreEqual(2, queue.Visible.Count, "The same text at a different severity is different news.");

        queue.Publish(new(NotificationSeverity.Warning, "Sync başarısız · etsy/price"), T0.AddMinutes(5));
        Assert.AreEqual(3, queue.Visible.Count, "Outside the dedupe window the repeat is fresh news again.");
    }

    [TestMethod]
    public void EachSeverityHasItsOwnClockAndAnErrorHasNone()
    {
        Assert.IsTrue(NotificationQueue.DurationFor(NotificationSeverity.Success) < NotificationQueue.DurationFor(NotificationSeverity.Info));
        Assert.IsTrue(NotificationQueue.DurationFor(NotificationSeverity.Info) < NotificationQueue.DurationFor(NotificationSeverity.Warning));
        Assert.IsNull(NotificationQueue.DurationFor(NotificationSeverity.Error), "An error waits for a person.");

        var queue = new NotificationQueue();
        queue.Publish(new(NotificationSeverity.Success, "Kaydedildi"), T0);
        var error = queue.Publish(new(NotificationSeverity.Error, "Yazma başarısız"), T0);
        queue.Publish(new(NotificationSeverity.Warning, "Kaynak bayat"), T0);

        var removed = queue.Expire(T0.AddHours(1));

        CollectionAssert.AreEquivalent(new[] { "Kaydedildi", "Kaynak bayat" }, removed.Select(t => t.Text).ToArray());
        Assert.AreEqual(1, queue.Visible.Count);
        Assert.AreEqual(NotificationSeverity.Error, queue.Visible[0].Severity, "An hour later the error is still there.");
        Assert.IsTrue(queue.Dismiss(error.Id));
        Assert.AreEqual(0, queue.Visible.Count, "Dismissed by a person, it goes.");
        Assert.IsFalse(queue.Dismiss(error.Id), "Dismissing twice is not an error, just nothing.");
    }

    [TestMethod]
    public void ExpiringAVisibleToastPromotesTheOldestPendingOne()
    {
        var queue = new NotificationQueue();
        for (var i = 1; i <= 5; i++) queue.Publish(new(NotificationSeverity.Success, $"İş {i}"), T0.AddMilliseconds(i));
        Assert.AreEqual(2, queue.PendingCount);

        var removed = queue.Expire(T0 + NotificationQueue.DurationFor(NotificationSeverity.Success)!.Value + TimeSpan.FromSeconds(1));

        Assert.AreEqual(3, removed.Count, "The first three had all run their clock.");
        CollectionAssert.AreEqual(new[] { "İş 4", "İş 5" }, queue.Visible.Select(t => t.Text).ToArray(), "The queue moves up in arrival order.");
        Assert.AreEqual(0, queue.PendingCount);
        Assert.IsTrue(queue.Visible.All(t => t.ShownUtc > T0.AddSeconds(1)), "A promoted toast's clock starts when it is shown, not when it was queued.");
    }

    [TestMethod]
    public void KeyboardDismissRemovesTheNewestVisibleToastFirst()
    {
        var queue = new NotificationQueue();
        queue.Publish(Info("Birinci"), T0);
        queue.Publish(Info("İkinci"), T0.AddSeconds(1));
        queue.Publish(Info("Üçüncü"), T0.AddSeconds(2));
        queue.Publish(Info("Dördüncü"), T0.AddSeconds(3));

        var dismissed = queue.DismissTop();

        Assert.AreEqual("Üçüncü", dismissed!.Text, "Escape closes what just appeared, the way a person expects.");
        CollectionAssert.AreEqual(new[] { "Birinci", "İkinci", "Dördüncü" }, queue.Visible.Select(t => t.Text).ToArray(), "Closing one lets the next pending one in.");

        queue.DismissAll();
        Assert.AreEqual(0, queue.Visible.Count);
        Assert.AreEqual(0, queue.PendingCount, "Dismiss all means the pending ones too; nobody wants seventeen more after clearing the screen.");
        Assert.IsNull(queue.DismissTop());
    }

    [TestMethod]
    public void TheTextNeverCarriesASecretPiiOrANewline()
    {
        var queue = new NotificationQueue();
        var toast = queue.Publish(new(NotificationSeverity.Error,
            "İstek reddedildi\r\nAuthorization: Bearer abc.def.ghi · access_token=XYZ123 · müşteri ali@example.com · C:\\Users\\serif\\feed.xml"), T0);

        foreach (var forbidden in new[] { "abc.def.ghi", "XYZ123", "ali@example.com", "\\serif\\", "\r", "\n" })
            Assert.IsFalse(toast.Text.Contains(forbidden, StringComparison.Ordinal), $"'{forbidden}' reached the toast: {toast.Text}");
        StringAssert.Contains(toast.Text, "[redacted]");
        StringAssert.Contains(toast.Text, "[pii-email]");

        var novel = queue.Publish(Info(new string('x', 5000)), T0);
        Assert.IsTrue(novel.Text.Length <= NotificationQueue.MaxTextLength, "A toast is a sentence, not a log.");
        StringAssert.EndsWith(novel.Text, "…");

        var blank = queue.Publish(Info("   "), T0);
        Assert.AreNotEqual("", blank.Text, "An empty toast is a bug made visible; it gets a generic sentence instead.");
    }

    [TestMethod]
    public void APendingOverflowDropsTheOldestNonErrorAndCountsIt()
    {
        var queue = new NotificationQueue();
        var error = queue.Publish(new(NotificationSeverity.Error, "Kritik"), T0);
        for (var i = 1; i <= NotificationQueue.MaxVisible + NotificationQueue.MaxPending + 5; i++)
            queue.Publish(Info($"Bilgi {i}"), T0.AddMilliseconds(i));

        Assert.AreEqual(NotificationQueue.MaxPending, queue.PendingCount, "The pending line has a ceiling.");
        Assert.IsTrue(queue.DroppedCount > 0, "What fell off the end is counted, not silently forgotten.");
        Assert.IsTrue(queue.Visible.Any(t => t.Id == error.Id), "An error is never the one that gets dropped.");
    }
}
