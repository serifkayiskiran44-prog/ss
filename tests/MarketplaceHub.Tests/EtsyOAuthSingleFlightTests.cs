#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #276 claim 3 (OAuth refresh calls can race): previously verified by reading MainWindow.AuthorizedAsync's code
// only, since MainWindow's HttpClient had no injection seam and CredentialStore had no test-safe directory
// (see the #276 re-verification and CredentialStore-testability entries in IMPLEMENTATION_STATUS.md). Both
// gaps are now closed (MainWindow(directory, httpClient) and CredentialStore.Save/Load(directory)), so this
// drives the real production path end-to-end with a fake token endpoint -- no real network, no real credentials.
[TestClass]
public sealed class EtsyOAuthSingleFlightTests
{
    [TestMethod]
    public void TwoConcurrentAuthorizedAsyncCallsOnAnExpiredTokenMakeExactlyOneRealRefreshRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "auth-race-" + Guid.NewGuid().ToString("N"));
        HttpClient? httpClient = null;
        Exception? failure = null;
        try
        {
            var expired = new EtsyCredentials("key", "secret", "expired-token", "123", "refresh-token", DateTimeOffset.UtcNow.AddSeconds(-10));
            CredentialStore.Save(expired, root);
            var handler = new SingleFlightTokenHandler(delayMs: 150);
            httpClient = new HttpClient(handler);
            var capturedHttp = httpClient;

            var thread = new Thread(() =>
            {
                try
                {
                    var window = new MainWindow(root, capturedHttp);
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                        var method = typeof(MainWindow).GetMethod("AuthorizedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var task1 = (Task<EtsyCredentials>)method.Invoke(window, null)!;
                        var task2 = (Task<EtsyCredentials>)method.Invoke(window, null)!;
                        var both = Task.WhenAll(task1, task2);
                        var frame = new DispatcherFrame();
                        both.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                        Dispatcher.PushFrame(frame);
                        if (both.IsFaulted) throw both.Exception!.InnerException ?? both.Exception!;

                        Assert.AreEqual(1, handler.PostCount, "Two overlapping AuthorizedAsync calls on the same expired credential must make exactly one real refresh request, not two.");
                        Assert.AreEqual("new-access", task1.Result.Token);
                        Assert.AreEqual("new-access", task2.Result.Token, "The second caller must observe the first caller's already-refreshed credential, not fire its own redundant refresh.");
                    }
                    finally { window.Close(); }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); thread.Join();
            if (failure != null) throw new AssertFailedException(failure.ToString());
        }
        finally
        {
            httpClient?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    sealed class SingleFlightTokenHandler(int delayMs) : HttpMessageHandler
    {
        public int PostCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Unexpected non-POST request: " + request.RequestUri);
            Interlocked.Increment(ref PostCount);
            await Task.Delay(delayMs, cancellationToken); // widens the race window deterministically so the second caller reaches the semaphore before the first's refresh resolves.
            var json = "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
