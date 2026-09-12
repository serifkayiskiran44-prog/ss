using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class SchedulerDecoupleTests
{
    [TestMethod]
    public void ScheduledTickRunsDueAutomationJobEvenWithNoDueXmlSource()
    {
        Run(f => {
            Assert.AreEqual(0, f.Sync.List().Count, "No sync job should exist before the tick runs.");
            f.InvokeScheduledAsync();
            var jobs = f.Sync.List();
            Assert.IsTrue(jobs.Any(x => x.Operation == "price"), "A due Price automation job must run on a tick even when there is no due XML source (#307).");
        });
    }

    [TestMethod]
    public void ScheduledTickDoesNothingWhenNeitherXmlNorAutomationIsDue()
    {
        Run(f => {
            var job = f.Automation.List().Single();
            job.NextRunUtc = DateTime.UtcNow.AddHours(1);
            f.Automation.Save(job);

            f.InvokeScheduledAsync();

            Assert.AreEqual(0, f.Sync.List().Count, "Nothing due (neither XML nor automation) must enqueue no work.");
        });
    }

    [TestMethod]
    public void ScheduledTickEvaluatesEachDueJobExactlyOnceAcrossTwoConsecutiveTicks()
    {
        Run(f => {
            f.InvokeScheduledAsync();
            var afterFirst = f.Sync.List().Count;
            Assert.IsTrue(afterFirst > 0);

            f.InvokeScheduledAsync();
            var afterSecond = f.Sync.List().Count;

            Assert.AreEqual(afterFirst, afterSecond, "A job whose NextRunUtc was advanced by the first tick must not run again on an immediate second tick.");
        });
    }

    static void Run(Action<Fixture> test)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { using var f = new Fixture(); test(f); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "scheduler-decouple-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window;
        public SyncStore Sync;
        public AutomationStore Automation;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" }; // AutoImport defaults to false: never due.
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 5 } });

            Automation = new AutomationStore(Root);
            Automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "etsy", Shop = "default" });

            Sync = new SyncStore(Root);
            Window = new MainWindow(Root);
        }

        public void InvokeScheduledAsync()
        {
            // ScheduledAsync awaits Task.Run internally and touches UI afterward; without a running Dispatcher
            // message loop the continuation would resume on a thread-pool thread and violate WPF's dispatcher
            // affinity. Install a DispatcherSynchronizationContext and pump a nested frame until it completes.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Window.Dispatcher));
            var method = typeof(MainWindow).GetMethod("ScheduledAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task)method.Invoke(Window, null);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) throw task.Exception!.InnerException ?? task.Exception!;
        }

        public void Dispose()
        {
            Window.Close();
            // Selecting the seeded XmlSource during MainWindow construction fires a fire-and-forget health check
            // (source-health.db) that can still be opening/using its connection after Dispose starts; the same
            // race affects other fixtures in this suite that seed an XmlSource (e.g. ProductCardDirtyDraftTests).
            // Re-clear pools on every attempt, since a connection opened after an earlier clear would otherwise
            // never be released before we give up.
            for (var attempt = 1; ; attempt++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(Root, true); return; }
                catch (IOException) when (attempt < 30) { Thread.Sleep(300); }
            }
        }
    }
}
