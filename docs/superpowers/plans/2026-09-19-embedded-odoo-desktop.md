# Embedded Odoo Desktop Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Turkish “Muhasebe / Odoo” workspace to the MonoBridge WPF application that starts the existing local Odoo runtime when needed and displays it inside the application without opening a browser.

**Architecture:** The Odoo installation publishes a non-secret runtime manifest under LocalAppData. The WPF application reads that manifest through a small runtime service, performs condition-based health checks, starts Odoo through its existing PowerShell entry point, and hosts the localhost UI in a WebView2 control with strict origin rules. The existing marketplace, XML, catalog, and credential flows remain unchanged.

**Tech Stack:** .NET 8 WPF, Microsoft.Web.WebView2 1.0.4191.47, MSTest, PowerShell 5.1, Odoo 19 Community, PostgreSQL 17

**Spec:** `docs/superpowers/specs/2026-09-19-embedded-odoo-desktop-design.md`

## Global Constraints

- Keep the existing .NET 8 WPF EXE as the primary application; do not add a MonoBridge login or password screen.
- Store runtime state under `%LOCALAPPDATA%`; never put passwords, API keys, tokens, cookies, or database secrets in the repository, manifest, test fixtures, logs, or UI messages.
- Odoo remains bound to `http://127.0.0.1:8069`; public signup stays disabled.
- Existing marketplace live-write gates and the seven `DEFERRED_BY_USER` areas are untouched.
- Tests use temporary directories, fake HTTP probes, and fake process launchers; they never write to real stores or the live Odoo database.
- Publish a new self-contained `win-x64` build to `Windows-Odoo-Embedded`; do not overwrite `Windows-Excel-Final`.
- Pin `Microsoft.Web.WebView2` to stable version `1.0.4191.47`, verified from the official NuGet package page on 2026-09-19.

## Review Focus

- A malformed or oversized runtime manifest must fail with a Turkish, non-secret diagnostic and must not start any process; Task 2 adds this test.
- Concurrent navigation to the Odoo page must launch at most one start process; Task 2 adds a single-flight test.
- A localhost URL containing user-info, a non-loopback host, or a different port must open outside the embedded view or be rejected; Task 3 adds URI-policy tests.
- A missing WebView2 Runtime must leave the rest of MonoBridge usable and show a Turkish recovery message; Task 4 adds the panel-state test.
- Closing MonoBridge during a pending health poll must cancel cleanly without stopping Odoo or surfacing an unhandled exception; Task 4 adds cancellation coverage.

---

### Task 1: Publish a non-secret Odoo runtime manifest

**Files:**
- Create: `../monobridge-odoo/scripts/write-runtime-manifest.ps1`
- Modify: `../monobridge-odoo/scripts/start-odoo.ps1`
- Create: `../monobridge-odoo/tests/test_runtime_manifest_contract.py`
- Modify: `../monobridge-odoo/README.md`

**Interfaces:**
- Consumes: `$PSScriptRoot`, `$env:LOCALAPPDATA`, existing `start-odoo.ps1`, `stop-odoo.ps1`, and `odoo.log` paths.
- Produces: `%LOCALAPPDATA%\MonoBridgeOdoo\runtime.json` with `schemaVersion`, `baseUrl`, `installRoot`, `startScriptPath`, `stopScriptPath`, `logPath`, and `postgresServiceName`.

- [ ] **Step 1: Write the failing manifest contract tests**

```python
def test_runtime_manifest_writer_contains_only_public_runtime_fields():
    script = (ROOT / "scripts" / "write-runtime-manifest.ps1").read_text("utf-8")
    for field in ("schemaVersion", "baseUrl", "installRoot", "startScriptPath", "stopScriptPath", "logPath", "postgresServiceName"):
        assert field in script
    for forbidden in ("runtime-secrets", "password", "apiKey", "token"):
        assert forbidden.lower() not in script.lower()

def test_start_script_refreshes_manifest_before_health_check():
    script = (ROOT / "scripts" / "start-odoo.ps1").read_text("utf-8")
    assert "write-runtime-manifest.ps1" in script
    assert "[switch]$NoBrowser" in script
    assert "if (-not $NoBrowser)" in script
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `python -m unittest discover -s tests -p "test_runtime_manifest_contract.py" -v`

Expected: FAIL because `write-runtime-manifest.ps1` does not exist and the start script does not call it.

- [ ] **Step 3: Implement the manifest writer**

```powershell
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $env:LOCALAPPDATA "MonoBridgeOdoo"
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$manifest = [ordered]@{
    schemaVersion = 1
    baseUrl = "http://127.0.0.1:8069"
    installRoot = $projectRoot
    startScriptPath = Join-Path $PSScriptRoot "start-odoo.ps1"
    stopScriptPath = Join-Path $PSScriptRoot "stop-odoo.ps1"
    logPath = Join-Path $runtimeDirectory "odoo.log"
    postgresServiceName = "postgresql-x64-17"
}
$temporary = Join-Path $runtimeDirectory "runtime.json.tmp"
$target = Join-Path $runtimeDirectory "runtime.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
Move-Item -LiteralPath $temporary -Destination $target -Force
```

Add `param([switch]$NoBrowser)` at the start of `start-odoo.ps1`, invoke the writer with `& (Join-Path $PSScriptRoot "write-runtime-manifest.ps1")` before `Test-OdooReady`, and replace the final unconditional browser launch with:

```powershell
if (-not $NoBrowser) {
    Start-Process $url
}
```

- [ ] **Step 4: Run focused and full Odoo tests**

Run: `python -m unittest discover -s tests -p "test_runtime_manifest_contract.py" -v`

Expected: PASS.

Run: `.\scripts\test.ps1`

Expected: all tests PASS.

- [ ] **Step 5: Generate and inspect the real manifest**

Run: `.\scripts\write-runtime-manifest.ps1`

Run: `Get-Content "$env:LOCALAPPDATA\MonoBridgeOdoo\runtime.json"`

Expected: seven public fields, localhost URL, valid absolute script paths, and no secret values.

- [ ] **Step 6: Commit the Odoo-side contract**

```powershell
git add scripts/write-runtime-manifest.ps1 scripts/start-odoo.ps1 tests/test_runtime_manifest_contract.py README.md
git commit -m "feat: publish local Odoo runtime manifest"
```

### Task 2: Add the desktop runtime loader and single-flight starter

**Files:**
- Create: `OdooRuntime.cs`
- Create: `tests/MarketplaceHub.Tests/OdooRuntimeTests.cs`

**Interfaces:**
- Consumes: runtime manifest from Task 1, `HttpClient`, `IProcessLauncher`, and a cancellation token.
- Produces: `OdooRuntimeManifest`, `OdooRuntimeResult`, `IOdooRuntimeService`, `IOdooHealthProbe`, `IOdooProcessLauncher`, `OdooRuntimeManifestStore.Load(string path)`, and `OdooRuntimeService.EnsureReadyAsync(CancellationToken)`.

- [ ] **Step 1: Write manifest validation tests**

```csharp
[TestMethod]
public void ManifestRejectsSecretsUnknownSchemaAndNonLoopbackUrls()
{
    var dir = TempDir();
    File.WriteAllText(Path.Combine(dir,"runtime.json"), """{"schemaVersion":1,"baseUrl":"https://example.com","startScriptPath":"x","stopScriptPath":"y","logPath":"z","postgresServiceName":"p","password":"secret"}""");
    var ex = Assert.ThrowsException<InvalidDataException>(() => OdooRuntimeManifestStore.Load(Path.Combine(dir,"runtime.json")));
    StringAssert.Contains(ex.Message, "geçersiz");
    Assert.IsFalse(ex.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
}
```

Also add cases for a missing file, a file larger than 64 KiB, schema version other than `1`, missing absolute script paths, and valid `127.0.0.1`/`localhost` URLs on port `8069`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter OdooRuntimeTests`

Expected: FAIL because the runtime types do not exist.

- [ ] **Step 3: Implement strict manifest loading**

```csharp
public sealed record OdooRuntimeManifest(
    int SchemaVersion, Uri BaseUrl, string InstallRoot, string StartScriptPath,
    string StopScriptPath, string LogPath, string PostgresServiceName);

public static class OdooRuntimeManifestStore
{
    public static OdooRuntimeManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 65_536) throw Invalid();
        // Deserialize with case-insensitive names, reject unknown properties,
        // require schema 1, loopback HTTP port 8069 and absolute local paths.
    }
}
```

Error text is Turkish and never includes raw JSON or field values.

- [ ] **Step 4: Write readiness and concurrency tests**

```csharp
[TestMethod]
public async Task ConcurrentEnsureReadyStartsOnceAndReturnsReady()
{
    var probe = new SequenceProbe(false, false, true);
    var launcher = new RecordingLauncher();
    var service = new OdooRuntimeService(ValidManifest(), probe, launcher, TimeSpan.FromMilliseconds(1), 5);
    var results = await Task.WhenAll(service.EnsureReadyAsync(default), service.EnsureReadyAsync(default));
    Assert.AreEqual(1, launcher.Count);
    Assert.IsTrue(results.All(x => x.IsReady));
}
```

Add tests for already healthy/no launch, timeout, launch failure with sanitized detail, and cancellation.

- [ ] **Step 5: Implement health probe, process launcher, and single-flight service**

```csharp
public interface IOdooHealthProbe { Task<bool> IsReadyAsync(Uri baseUrl, CancellationToken cancellationToken); }
public interface IOdooProcessLauncher { Task StartAsync(string scriptPath, CancellationToken cancellationToken); }
public interface IOdooRuntimeService { Task<OdooRuntimeResult> EnsureReadyAsync(CancellationToken cancellationToken); }
public sealed record OdooRuntimeResult(bool IsReady, string Message, string? LogPath = null);

public sealed class OdooRuntimeService : IOdooRuntimeService
{
    readonly SemaphoreSlim gate = new(1,1);
    Task<OdooRuntimeResult>? activeStart;
    public async Task<OdooRuntimeResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Task<OdooRuntimeResult> operation;
        await gate.WaitAsync(cancellationToken);
        try { operation = activeStart ??= EnsureReadyCoreAsync(); }
        finally { gate.Release(); }
        try { return await operation.WaitAsync(cancellationToken); }
        finally
        {
            if (operation.IsCompleted)
            {
                await gate.WaitAsync(CancellationToken.None);
                try { if (ReferenceEquals(activeStart, operation)) activeStart = null; }
                finally { gate.Release(); }
            }
        }
    }

    async Task<OdooRuntimeResult> EnsureReadyCoreAsync()
    {
        if (await probe.IsReadyAsync(manifest.BaseUrl, CancellationToken.None)) return new(true, "Odoo hazır.");
        await launcher.StartAsync(manifest.StartScriptPath, CancellationToken.None);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(pollInterval);
            if (await probe.IsReadyAsync(manifest.BaseUrl, CancellationToken.None)) return new(true, "Odoo hazır.");
        }
        return new(false, "Odoo zamanında başlatılamadı.", manifest.LogPath);
    }
}
```

The production launcher runs `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File <validated path>` with `UseShellExecute=false` and no redirected secret-bearing output. The health probe requests `/web/login` with a three-second request timeout. Poll up to 30 times at two-second intervals and use the supplied cancellation token.

- [ ] **Step 6: Run focused tests and commit**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter OdooRuntimeTests`

Expected: PASS.

```powershell
git add OdooRuntime.cs tests/MarketplaceHub.Tests/OdooRuntimeTests.cs
git commit -m "feat: manage local Odoo runtime"
```

### Task 3: Enforce embedded navigation boundaries

**Files:**
- Create: `OdooNavigationPolicy.cs`
- Create: `tests/MarketplaceHub.Tests/OdooNavigationPolicyTests.cs`

**Interfaces:**
- Consumes: candidate `Uri` values from WebView2 navigation and new-window events.
- Produces: `OdooNavigationDecision` and `OdooNavigationPolicy.Decide(Uri uri)`.

- [ ] **Step 1: Write URI boundary tests**

```csharp
[DataTestMethod]
[DataRow("http://127.0.0.1:8069/web", true)]
[DataRow("http://localhost:8069/web", true)]
[DataRow("http://127.0.0.1:8070/web", false)]
[DataRow("https://127.0.0.1:8069/web", false)]
[DataRow("http://user@127.0.0.1:8069/web", false)]
[DataRow("https://odoo.com", false)]
public void OnlyExactLocalOdooOriginStaysEmbedded(string value, bool embedded)
{
    Assert.AreEqual(embedded, OdooNavigationPolicy.Decide(new Uri(value)).OpenEmbedded);
}
```

Add cases for IPv6 loopback, fragments, uppercase localhost, malformed absolute input, and `javascript:`/`file:` schemes.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter OdooNavigationPolicyTests`

Expected: FAIL because the policy does not exist.

- [ ] **Step 3: Implement an exact-origin policy**

```csharp
public sealed record OdooNavigationDecision(bool OpenEmbedded, bool OpenExternal);

public static class OdooNavigationPolicy
{
    public static OdooNavigationDecision Decide(Uri uri)
    {
        var local = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp &&
            string.IsNullOrEmpty(uri.UserInfo) && uri.Port == 8069 &&
            (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        return new(local, !local && uri.Scheme is "http" or "https");
    }
}
```

Non-HTTP(S) external schemes are rejected instead of launched.

- [ ] **Step 4: Run focused tests and commit**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter OdooNavigationPolicyTests`

Expected: PASS.

```powershell
git add OdooNavigationPolicy.cs tests/MarketplaceHub.Tests/OdooNavigationPolicyTests.cs
git commit -m "feat: restrict embedded Odoo navigation"
```

### Task 4: Add the WebView2 workspace and navigation entry

**Files:**
- Modify: `TrMarketplaceHubDesktop.csproj`
- Create: `OdooWorkspacePanel.cs`
- Modify: `MainWindow.Navigation.cs`
- Modify: `ScreenParityAudit.cs`
- Create: `tests/MarketplaceHub.Tests/OdooWorkspacePanelTests.cs`

**Interfaces:**
- Consumes: `IOdooRuntimeService`, `OdooRuntimeManifest`, and `OdooNavigationPolicy` from Tasks 2–3.
- Produces: `OdooWorkspacePanel : UserControl, IDisposable` and the `odoo` application route labeled `Muhasebe / Odoo`.

- [ ] **Step 1: Pin WebView2 and write the failing panel tests**

Add to `TrMarketplaceHubDesktop.csproj`:

```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4191.47" />
```

Tests must assert that:

```csharp
[TestMethod]
public void OdooIsVisibleUnderManagementAndHasRecoveryControls()
{
    InSta(dir => {
        var window = new MainWindow(dir);
        var nav = Walk(window).OfType<ListBox>().Single(x => x.Name == "NavigationList");
        Assert.IsTrue(nav.Items.OfType<ListBoxItem>().Any(x => Equals(x.Tag,"odoo") && Equals(x.Content,"Muhasebe / Odoo")));
        var panel = Walk(window).OfType<FrameworkElement>().Single(x => x.Name == "OdooWorkspace");
        Assert.IsNotNull(Walk(panel).OfType<Button>().Single(x => x.Name == "OdooRetry"));
        window.Close();
    });
}
```

Use an injected fake `IOdooRuntimeService` and an injected `IOdooEmbeddedBrowser` adapter in tests so constructing `MainWindow` performs no process or network action. Add a test where the adapter initialization throws `WebView2RuntimeNotFoundException`; the panel stays visible, shows the Turkish runtime message, and does not throw. Add a disposal test that cancels a pending readiness task.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter OdooWorkspacePanelTests`

Expected: FAIL because the panel and navigation route do not exist.

- [ ] **Step 3: Implement the stateful WPF panel**

```csharp
public interface IOdooEmbeddedBrowser
{
    FrameworkElement View { get; }
    Task InitializeAsync(string userDataFolder, CancellationToken cancellationToken);
    void Navigate(Uri uri);
}

public sealed class OdooWorkspacePanel : UserControl, IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly Grid root = new() { Name = "OdooWorkspace" };
    readonly Button retry = new() { Name = "OdooRetry", Content = "Yeniden dene" };
    readonly TextBlock status = new() { Text = "Odoo hazırlanıyor…", TextWrapping = TextWrapping.Wrap };
    readonly IOdooEmbeddedBrowser browser;

    internal OdooWorkspacePanel(IOdooRuntimeService runtime, OdooRuntimeManifest manifest, IOdooEmbeddedBrowser browser)
    {
        this.browser = browser;
        root.Children.Add(browser.View);
        root.Children.Add(status);
        root.Children.Add(retry);
        Content = root;
        Loaded += async (_,_) => await LoadAsync(runtime, manifest);
        retry.Click += async (_,_) => await LoadAsync(runtime, manifest);
    }

    async Task LoadAsync(IOdooRuntimeService runtime, OdooRuntimeManifest manifest)
    {
        status.Text = "Odoo hazırlanıyor…";
        var result = await runtime.EnsureReadyAsync(lifetime.Token);
        if (!result.IsReady) { status.Text = result.Message; retry.Visibility = Visibility.Visible; return; }
        await browser.InitializeAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "WebView2", "Odoo"), lifetime.Token);
        browser.Navigate(new Uri(manifest.BaseUrl, "/web/login"));
        status.Visibility = Visibility.Collapsed;
        retry.Visibility = Visibility.Collapsed;
    }
    public void Dispose() { lifetime.Cancel(); (browser as IDisposable)?.Dispose(); lifetime.Dispose(); }
}
```

Initialize WebView2 with `%LOCALAPPDATA%\MonoBridgeDesktop\WebView2\Odoo`. Disable dev tools in Release, reject camera/microphone/geolocation permission requests, apply Task 3 to `NavigationStarting` and `NewWindowRequested`, and call `Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })` only for approved external HTTP(S) links.

The panel begins readiness work only on `Loaded`, not in its constructor. It updates WPF state through the dispatcher, cancels on `Unloaded`/`Dispose`, and never stops Odoo during normal application shutdown.

- [ ] **Step 4: Register the route and disposal**

In `BuildNavigation()` add under `YÖNETİM`:

```csharp
var odooWorkspace = OdooWorkspacePanel.CreateDefault();
Page("odoo", "Muhasebe / Odoo", "Muhasebe, fatura, depo ve Odoo yönetimini uygulama içinde açın.", odooWorkspace);
Closed += (_,_) => odooWorkspace.Dispose();
```

Add `odoo` to `ScreenParityAudit.RequiredRoutes` so route loss fails the existing parity contract.

- [ ] **Step 5: Run focused UI tests and commit**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "OdooWorkspacePanelTests|OdooNavigationPolicyTests|OdooRuntimeTests"`

Expected: PASS without opening a browser, starting Odoo, or touching the real user data directory.

```powershell
git add TrMarketplaceHubDesktop.csproj OdooWorkspacePanel.cs MainWindow.Navigation.cs ScreenParityAudit.cs tests/MarketplaceHub.Tests/OdooWorkspacePanelTests.cs
git commit -m "feat: embed Odoo workspace in desktop app"
```

### Task 5: Update project status and run complete validation

**Files:**
- Modify: `PROJECT_SPEC.md`
- Modify: `TODO.md`
- Modify: `IMPLEMENTATION_STATUS.md`
- Modify: `README-YONETIM-MERKEZI.md`

**Interfaces:**
- Consumes: final behavior from Tasks 1–4.
- Produces: accurate Turkish operator documentation without claiming marketplace, NES, or accounting data synchronization that is not implemented.

- [ ] **Step 1: Document the exact delivered behavior**

Add concise entries stating:

```text
Muhasebe / Odoo: yerel Odoo 19 Community arayüzü MonoBridge içinde WebView2 ile açılır.
MonoBridge yerel çalışma bildirimini okuyup Odoo'yu gerektiğinde başlatır.
İlk Odoo oturumu sonrasında WebView2 profili oturumu korur.
Bu ekran mevcut SQLite ürün/sipariş verisini kendiliğinden Odoo'ya aktarmış sayılmaz.
```

Record the remaining separate work: catalog/order/stock/accounting synchronization and NES live operations.

- [ ] **Step 2: Run the complete Release suite**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release`

Expected: all tests PASS with zero failures.

- [ ] **Step 3: Run secret and diff checks**

Run: `git diff --check`

Run a repository search for known user-supplied API keys/passwords and for `runtime-secrets.json` references in the new WPF files.

Expected: no secret values and no desktop dependency on the secret file.

- [ ] **Step 4: Commit documentation**

```powershell
git add PROJECT_SPEC.md TODO.md IMPLEMENTATION_STATUS.md README-YONETIM-MERKEZI.md
git commit -m "docs: describe embedded Odoo workspace"
```

### Task 6: Publish, launch, and visually verify the new desktop build

**Files:**
- Output: `Windows-Odoo-Embedded/TrMarketplaceHubDesktop.exe`

**Interfaces:**
- Consumes: completed WPF application and the installed local Odoo runtime.
- Produces: a self-contained Windows desktop release with embedded Odoo navigation.

- [ ] **Step 1: Ensure the Odoo runtime is healthy**

Run from `../monobridge-odoo`:

```powershell
.\scripts\write-runtime-manifest.ps1
.\scripts\start-odoo.ps1 -NoBrowser
```

Verify: `Invoke-WebRequest -UseBasicParsing http://127.0.0.1:8069/web/login` returns `200`; `/web/signup` returns `404`.

- [ ] **Step 2: Publish to a new directory**

Run:

```powershell
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Odoo-Embedded
```

Expected: exit code `0`, EXE plus WebView2 loader/runtime assets present.

- [ ] **Step 3: Launch the published application**

Run:

```powershell
Start-Process -FilePath .\Windows-Odoo-Embedded\TrMarketplaceHubDesktop.exe -WorkingDirectory .\Windows-Odoo-Embedded
```

Expected: one visible MonoBridge window and no separately opened Odoo browser window.

- [ ] **Step 4: Perform the visual smoke test**

Verify in the published GUI:

- `Muhasebe / Odoo` appears under `YÖNETİM`.
- Selecting it shows the local Odoo login or active session inside the MonoBridge content area.
- The retry/status UI remains readable at the current maximized window size.
- Navigating between Product Management and Odoo preserves both screens.
- Closing and reopening MonoBridge preserves a still-valid Odoo session.
- No signup link is visible and no external browser opens for local Odoo navigation.

- [ ] **Step 5: Final repository and artifact check**

Run `git status --short` in both repositories. Confirm only known pre-existing unrelated desktop changes remain uncommitted, and every new Odoo-embedding source file is committed. Record the exact test count, publish directory, Odoo HTTP checks, and commit hashes in the final report.
