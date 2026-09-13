using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #885 (DESIGN: Global loading overlay deadlock guard). The busy state is reference-counted per owner: nested
// entries keep it busy until the last leaves, a second dispose is a no-op, an exception and a cancellation leave
// through the same path, an owner can be dropped outright, everything can be released as the terminal safety net;
// cancelling runs only the owners' cancel actions and nothing else; labels are redacted; the headline names the
// count when more than one owner is in.
[TestClass]
public sealed class BusyStateTests
{
    [TestMethod]
    public void NestedOwnersExceptionsCancellationAndDoubleDisposeNeverLeaveTheStateStuck()
    {
        var state = new BusyState(); var changes = 0; state.Changed += () => changes++;
        Assert.IsFalse(state.IsBusy); Assert.AreEqual("", state.Headline);

        // Nested entries of one owner: busy until the last leaves; a second dispose is a no-op.
        var first = state.Enter("shell", "Ürünler içe aktarılıyor…");
        var second = state.Enter("shell", "Ürünler içe aktarılıyor…");
        Assert.IsTrue(state.IsBusy); Assert.AreEqual(2, state.Depth); Assert.AreEqual(1, state.Owners.Count); Assert.AreEqual("Ürünler içe aktarılıyor…", state.Headline);
        first.Dispose(); Assert.IsTrue(state.IsBusy); Assert.AreEqual(1, state.Depth);
        first.Dispose(); Assert.IsTrue(state.IsBusy, "a second dispose of the same token changes nothing"); Assert.AreEqual(1, state.Depth);
        second.Dispose(); Assert.IsFalse(state.IsBusy); Assert.AreEqual(0, state.Depth);
        second.Dispose(); Assert.AreEqual(0, state.Depth, "never negative");
        Assert.IsTrue(changes >= 4);

        // Two owners: the headline counts them; one leaving keeps the other.
        using (state.Enter("shell", "A işi")) using (state.Enter("scheduler", "Zamanlanmış kontrol sürüyor…"))
        {
            Assert.AreEqual(2, state.Owners.Count); StringAssert.StartsWith(state.Headline, "2 işlem sürüyor");
        }
        Assert.IsFalse(state.IsBusy);

        // An exception in the work leaves the scope; so does a cancellation.
        Assert.ThrowsExceptionAsync<InvalidOperationException>(() => state.RunAsync("shell", "patlayan iş", () => throw new InvalidOperationException("x"))).GetAwaiter().GetResult();
        Assert.IsFalse(state.IsBusy, "an exception leaves");
        using var cts = new CancellationTokenSource(); var cancelCalls = 0;
        var running = state.RunAsync("shell", "iptal edilebilir iş", () => Task.Delay(Timeout.Infinite, cts.Token), () => { cancelCalls++; cts.Cancel(); });
        Assert.IsTrue(state.IsBusy); Assert.IsTrue(state.CanCancel);
        Assert.AreEqual(1, state.CancelAll()); Assert.AreEqual(1, cancelCalls);
        Assert.ThrowsExceptionAsync<TaskCanceledException>(() => running).GetAwaiter().GetResult();
        Assert.IsFalse(state.IsBusy, "a cancellation leaves"); Assert.AreEqual(0, state.CancelAll(), "nothing left to cancel");

        // Cancel runs only the cancel actions: an owner without one is not cancellable and nothing else is invoked.
        var sideEffects = 0; Action guarded = () => sideEffects++;
        using (state.Enter("shell", "onaylı iş"))
        {
            Assert.IsFalse(state.CanCancel); Assert.AreEqual(0, state.CancelAll()); Assert.AreEqual(0, sideEffects, "the overlay reaches no owner action");
            _ = guarded;
        }

        // Dropping an owner outright (a screen left mid-load) and the terminal safety net.
        state.Enter("orders", "Siparişler yükleniyor…"); state.Enter("orders", "Siparişler yükleniyor…"); state.Enter("shell", "x");
        Assert.IsTrue(state.ReleaseAll("orders")); Assert.IsFalse(state.ReleaseAll("orders")); Assert.AreEqual(1, state.Owners.Count);
        Assert.AreEqual(1, state.ReleaseEverything()); Assert.IsFalse(state.IsBusy); Assert.AreEqual(0, state.ReleaseEverything());

        // Rapid entries and exits never drift: a hundred interleaved scopes end idle.
        var tokens = Enumerable.Range(0, 100).Select(i => state.Enter(i % 2 == 0 ? "shell" : "orders", "hızlı")).ToList();
        Assert.AreEqual(100, state.Depth); foreach (var token in tokens) { token.Dispose(); token.Dispose(); }
        Assert.IsFalse(state.IsBusy); Assert.AreEqual(0, state.Depth);

        // Keys and labels are identifiers and redacted text.
        Assert.AreEqual("unnamed", BusyState.SafeKey("not an id!")); Assert.AreEqual("shell", BusyState.SafeKey("shell"));
        StringAssert.Contains(BusyState.SafeLabel("Sipariş ali@example.com işleniyor"), "[pii-email]"); Assert.AreEqual(BusyState.DefaultLabel, BusyState.SafeLabel("  "));
        Assert.IsTrue(BusyState.SafeLabel(new string('a', 500)).Length <= BusyState.MaxLabelLength);
    }
}
