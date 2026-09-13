using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #892 in the real window: the access check of a protected feed without a saved credential reads as "eksik"; the
// real download refuses with the typed reason and records the state; a saved credential turns the probe healthy
// and the state valid; a wrong one is "reddedildi · yenileme gerekli"; the state survives a restart and the source
// list names it; a credential saved for one source is never sent for another; and no value ever reaches the
// screen, the source record or the audit trail.
[TestClass]
public sealed class SourceCredentialHealthUiTests
{
    [TestMethod]
    public void TheHealthSurfacesTellMissingInvalidAndValidCredentialsApartWithoutShowingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "cred-ui-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var protectedId = Guid.NewGuid().ToString("N"); var openId = Guid.NewGuid().ToString("N");
                var catalog = new CatalogStore(root);
                catalog.SaveSource(new XmlSource { Id = protectedId, Name = "Korumalı besleme", Location = "https://feed.example.com/protected.xml", ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code" } });
                catalog.SaveSource(new XmlSource { Id = openId, Name = "Açık besleme", Location = "https://feed.example.com/open.xml", ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code" } });
                SqliteConnection.ClearAllPools();
                var server = new FeedServer("operator", "s3cret-value");
                window = new MainWindow(root, new HttpClient(server)); window.Show(); Drain(window);
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)Field(window, "sources");
                XmlSource Persisted(string id) => new CatalogStore(root).Sources().Single(x => x.Id == id);
                void Select(string id) { sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(x => x.Id == id); Drain(window); }
                string HealthText() => string.Join(" | ", Descendants((DependencyObject)((Dictionary<string, TabItem>)Field(window, "routes"))["xml"].Content).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text));
                void Check() { var task = (Task)typeof(MainWindow).GetMethod("CheckSourceReachabilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!; WaitUntil(window, () => task.IsCompleted, "the access check"); if (task.IsFaulted) throw task.Exception!.GetBaseException(); }

                // No credential saved, the server asks for one: the probe says the credential is missing and the list names it.
                Select(protectedId); Check();
                Assert.AreEqual(SourceCredentialHealth.Missing, Persisted(protectedId).LastCredentialState);
                Assert.IsTrue(server.Requests.All(r => r.Authorization is null), "nothing was sent");
                StringAssert.Contains(HealthText(), "Kimlik bilgisi"); StringAssert.Contains(HealthText(), "eksik");

                // The real download refuses with the typed reason and keeps the state.
                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                WaitUntil(window, () => inspect.IsCompleted, "the refused read");
                Assert.IsInstanceOfType(inspect.Exception!.GetBaseException(), typeof(XmlSourceAuthException));
                StringAssert.Contains(inspect.Exception.GetBaseException().Message, "kayıtlı kullanıcı adı ve şifre yok");
                Assert.AreEqual(SourceCredentialHealth.Missing, Persisted(protectedId).LastCredentialState);

                // A wrong credential saved: the probe sends it, the server rejects it, the state is a renewal.
                XmlAuthStore.Save(protectedId, new XmlAuth("operator", "wrong-value"), root);
                Check();
                Assert.AreEqual(SourceCredentialHealth.Invalid, Persisted(protectedId).LastCredentialState);
                Assert.IsTrue(server.Requests.Last().Authorization is { Scheme: "Basic" }, "the saved credential was sent");
                StringAssert.Contains(HealthText(), "yenileme gerekli");

                // The right credential: healthy and valid.
                XmlAuthStore.Save(protectedId, new XmlAuth("operator", "s3cret-value"), root);
                Check();
                Assert.AreEqual(SourceCredentialHealth.Valid, Persisted(protectedId).LastCredentialState); Assert.AreEqual("HEALTHY", Persisted(protectedId).LastHealthState);
                StringAssert.Contains(HealthText(), "doğrulandı");

                // The open feed never receives the protected feed's credential.
                Select(openId); Check();
                Assert.AreEqual(SourceCredentialHealth.NotNeeded, Persisted(openId).LastCredentialState);
                Assert.IsNull(server.Requests.Last().Authorization, "another source's credential is never sent");

                // Nothing on screen, in the source record or in the audit trail carries the value.
                var screen = HealthText();
                Assert.IsFalse(screen.Contains("s3cret-value") || screen.Contains("wrong-value"), screen);
                Assert.IsFalse(Directory.EnumerateFiles(root, "*.db").Any(f => ReadShared(f).Contains("s3cret-value")), "no database carries the value");
                Assert.IsFalse(new AuditStore(root).List(100).Any(e => e.Detail.Contains("s3cret-value") || e.Detail.Contains("wrong-value")), "the audit trail carries no value");

                // Restart: the state comes back with the source and shows before any probe; a wrong credential from the last session is a renewal the scheduled read fails fast on.
                XmlAuthStore.Save(protectedId, new XmlAuth("operator", "wrong-value"), root); Select(protectedId); Check();
                window.Close(); Drain(window); window = null; SqliteConnection.ClearAllPools();
                var requestsBefore = server.Requests.Count;
                window = new MainWindow(root, new HttpClient(server)); window.Show(); Drain(window);
                Navigate(window, "xml"); Drain(window); Select(protectedId);
                Assert.AreEqual(SourceCredentialHealth.Invalid, Persisted(protectedId).LastCredentialState);
                StringAssert.Contains(HealthText(), "yenileme gerekli");
                Assert.AreEqual(requestsBefore, server.Requests.Count, "showing the state costs no request");
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    /// <summary>A feed server: the protected path wants Basic auth, the open path wants none; every request's authorization header is recorded (scheme only matters).</summary>
    sealed class FeedServer(string user, string password) : HttpMessageHandler
    {
        public readonly List<(string Path, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization)> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization));
            var feed = new StringContent("<Products><Product><Code>S1</Code></Product></Products>", System.Text.Encoding.UTF8, "application/xml");
            if (request.RequestUri.AbsolutePath.EndsWith("open.xml", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = feed });
            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + password));
            if (request.Headers.Authorization is { Scheme: "Basic" } h && h.Parameter == expected) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = feed });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") });
        }
    }

    /// <summary>Reads a file the window may still hold open (SQLite keeps its handle); the bytes are scanned as text for a value that must not be there.</summary>
    static string ReadShared(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
