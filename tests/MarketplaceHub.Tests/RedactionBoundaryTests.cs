using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #894 fix-up: the central redaction masks a phone number that stands on its own, never a digit run that sits inside
// a hex or alphanumeric token -- a GUID id, a feed hash, a SKU -- which the preference guard would otherwise refuse
// as personal data (the CI run 34775205975 hit exactly such a random id).
[TestClass]
public sealed class RedactionBoundaryTests
{
    [TestMethod]
    public void APhoneStandsAloneButADigitRunInsideATokenIsNotOne()
    {
        Assert.AreEqual("Tel: [pii-phone]", AuditStore.Redact("Tel: 0532 123 45 67"));
        Assert.AreEqual("[pii-phone]", AuditStore.Redact("05321234567"));
        Assert.AreEqual("ara: [pii-phone].", AuditStore.Redact("ara: +90 532 123 45 67."));
        foreach (var token in new[] { "a05321234567b", "f0532123456789e0c1", "SKU05321234567X", "3f0532123456789abc0de1f2a3b4c5d6", "hash05321234567ff" })
            Assert.AreEqual(token, AuditStore.Redact(token), token);
        for (var i = 0; i < 2000; i++) { var id = Guid.NewGuid().ToString("N"); Assert.AreEqual(id, AuditStore.Redact(id), "a GUID id is never personal data: " + id); }
        Assert.AreEqual("kimlik 3f0532123456789abc0de1f2a3b4c5d6 ve [pii-phone]", AuditStore.Redact("kimlik 3f0532123456789abc0de1f2a3b4c5d6 ve 05321234567"));
        Assert.AreEqual("SKU-[pii-phone]", AuditStore.Redact("SKU-05321234567"), "a digit run after a separator still stands on its own");
    }
}
