using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #854 (DESIGN: Settings section dirty-state badges). A form registers fields with getters, snapshots on load and
// save, recomputes on edit; the dirty section names fields by label only -- a secret contributes presence and reads
// "(gizli)"; several sections order by when they went dirty; the change event fires on transitions, not on every
// keystroke; clearing resets a section; a fresh state (a restart) is clean.
[TestClass]
public sealed class SettingsEditStateTests
{
    [TestMethod]
    public void FieldsAreTrackedByLabelSecretsByPresenceAndSectionsOrderByWhenTheyWentDirty()
    {
        var state = new SettingsEditState(); var raised = 0; state.Changed += () => raised++;
        var vat = "20"; var date = "dd.MM.yyyy"; var secret = ""; var key = "k";
        var locale = state.Form("locale").Track("KDV %", () => vat).Track("Tarih", () => date);
        var trendyol = state.Form("trendyol-connection").Track("API key", () => key).Track("API secret", () => secret, secret: true);
        locale.Snapshot(); trendyol.Snapshot();
        Assert.IsFalse(state.Any); Assert.AreEqual(0, raised);

        vat = "18"; locale.Recompute(); locale.Recompute();
        Assert.AreEqual(1, raised, "The event fires when the summary changes, not on every recompute.");
        var dirty = state.Dirty("locale")!; Assert.AreEqual("Kaydedilmemiş değişiklik: KDV %", dirty.Summary); Assert.IsFalse(dirty.Fields[0].Secret);
        date = "yyyy-MM-dd"; locale.Recompute(); Assert.AreEqual(2, raised); Assert.AreEqual("Kaydedilmemiş değişiklik: KDV %, Tarih", state.Dirty("locale")!.Summary);
        var since = state.Dirty("locale")!.SinceUtc;

        secret = "s3cr3t-value"; trendyol.Recompute();
        var connection = state.Dirty("trendyol-connection")!;
        Assert.AreEqual("Kaydedilmemiş değişiklik: API secret (gizli)", connection.Summary); Assert.IsTrue(connection.Fields.Single().Secret);
        Assert.IsFalse(connection.Summary.Contains("s3cr3t"), "A secret's value never reaches the state.");
        secret = "another-value"; trendyol.Recompute(); Assert.AreEqual(3, raised, "A different secret of the same presence is no new transition.");
        CollectionAssert.AreEqual(new[] { "locale", "trendyol-connection" }, state.All.Select(d => d.EntryKey).ToArray(), "Sections order by when they went dirty.");
        Assert.IsTrue(state.All[0].SinceUtc <= state.All[1].SinceUtc); Assert.AreEqual(since, state.Dirty("locale")!.SinceUtc, "The first dirty moment is kept while the section stays dirty.");

        vat = "20"; date = "dd.MM.yyyy"; locale.Recompute();
        Assert.IsNull(state.Dirty("locale"), "Back to the saved values is clean again."); Assert.AreEqual(4, raised);
        state.Clear("trendyol-connection");
        Assert.IsNull(state.Dirty("trendyol-connection")); Assert.IsFalse(state.Any); Assert.AreEqual(5, raised);
        secret = ""; trendyol.Recompute(); Assert.AreEqual("Kaydedilmemiş değişiklik: API secret (gizli)", state.Dirty("trendyol-connection")!.Summary, "Clearing a saved secret is a change too.");

        Assert.AreSame(locale, state.Form("LOCALE"), "One tracker per section, case-insensitive.");
        Assert.ThrowsException<ArgumentException>(() => state.Form(" ")); Assert.ThrowsException<ArgumentException>(() => locale.Track("", () => "x"));
        var throwing = state.Form("x").Track("Alan", () => throw new InvalidOperationException("boom")); throwing.Snapshot(); Assert.IsNull(throwing.Recompute(), "A getter that throws reads as empty, never as a crash.");
        Assert.IsFalse(new SettingsEditState().Any, "A fresh state -- a restart without a save -- is clean.");
    }
}
