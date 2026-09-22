# MonoBridge Repository Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove reproducible workspace clutter and proven dead material while preserving all user data, active integrations, and in-progress source changes.

**Architecture:** Treat cleanup as a guarded release operation. First make generated output classification reproducible through ignore rules, then remove only verified generated paths, audit source and documents conservatively, and finally rebuild the package from zero and observe a visible WPF window.

**Tech Stack:** Windows PowerShell 5.1, Git, .NET 8, WPF, MSTest

**Spec:** `docs/superpowers/specs/2026-09-22-repository-cleanup-design.md`

## Global Constraints

- Do not modify or delete `%LOCALAPPDATA%\MonoBridgeDesktop`.
- Do not delete `.worktrees/` or any path returned by `git worktree list`.
- Do not reset existing modified source files.
- Remove code only when production, XAML, reflection, test, and documented entry-point searches all show it is unreachable.
- A fresh self-contained `win-x64` package must pass the visible-window smoke test.

---

### Task 1: Make generated output classification explicit

**Files:**
- Modify: `.gitignore`
- Test: PowerShell assertions against `git check-ignore`

**Interfaces:**
- Consumes: existing root output conventions (`bin/`, `obj/`, `Windows-*/`, `*.zip`)
- Produces: an explicit `artifacts/` ignore rule used by the package build

- [ ] **Step 1: Verify the current omission**

Run:

```powershell
git check-ignore -q artifacts
if ($LASTEXITCODE -eq 0) { throw 'artifacts is unexpectedly already ignored' }
```

Expected: command throws because `artifacts/` is not currently ignored.

- [ ] **Step 2: Add the minimal ignore rule**

Append this line beside the other build-output entries:

```gitignore
artifacts/
```

- [ ] **Step 3: Verify ignore behavior**

Run:

```powershell
git check-ignore -q artifacts
if ($LASTEXITCODE -ne 0) { throw 'artifacts ignore rule is not active' }
```

Expected: PASS.

- [ ] **Step 4: Commit**

```powershell
git add -- .gitignore
git commit -m "chore: ignore generated release artifacts"
```

### Task 2: Remove reproducible output safely

**Files:**
- Delete: `bin/`, `obj/`, `artifacts/`, root `Windows-*`, root release ZIP files
- Preserve: `.worktrees/`, source files, `%LOCALAPPDATA%\MonoBridgeDesktop`

**Interfaces:**
- Consumes: ignore rules and `git worktree list`
- Produces: a clean workspace ready for a from-zero build

- [ ] **Step 1: Resolve and validate every deletion target**

Use `GetFullPath`, require every resolved target to start with the repository root plus a directory separator, reject `.git`, `.worktrees`, and all registered worktree paths, and print the final target list with byte totals.

- [ ] **Step 2: Remove only validated targets**

Use `Remove-Item -LiteralPath` for each verified target. Use `-Recurse -Force` only for verified directories. Remove root ZIP files only when `git ls-files -- <path>` returns empty.

- [ ] **Step 3: Verify protected paths and working changes**

Run `git worktree list`, `git status --short`, and confirm `%LOCALAPPDATA%\MonoBridgeDesktop` still exists. Existing modified files must remain listed exactly as before cleanup.

### Task 3: Perform evidence-based source and documentation cleanup

**Files:**
- Inspect: tracked `*.cs`, `*.xaml`, `*.ps1`, `TODO.md`, `IMPLEMENTATION_STATUS.md`
- Modify only candidates proven unused or exact duplicate material

**Interfaces:**
- Consumes: `rg`, compiler output, project file inclusion rules
- Produces: behavior-preserving cleanup or a retained-candidates report when deletion cannot be proven safe

- [ ] **Step 1: Build from the cleaned tree and capture warnings**

Run:

```powershell
dotnet build .\TrMarketplaceHubDesktop.csproj -c Release --nologo
```

Expected: zero errors. Record actionable warnings.

- [ ] **Step 2: Audit candidate symbols before changing them**

For every candidate, search its type name, file name, XAML reference, navigation registration, test reference, and documentation reference with `rg`. Retain any candidate with an uncertain runtime or compatibility role.

- [ ] **Step 3: Apply only mechanically safe cleanup**

Remove unused local/script variables and exact duplicate documentation blocks. Do not delete connectors, compatibility migrations, audit gates, or tests merely because they are not currently visible in navigation.

- [ ] **Step 4: Run focused tests for each changed production file**

Run the tests naming the changed component. If no safe production-code deletion is found, record that result and do not manufacture code churn.

- [ ] **Step 5: Commit evidence-backed cleanup, if any**

Stage only files changed by this task and commit with:

```powershell
git commit -m "refactor: remove verified repository clutter"
```

### Task 4: Full verification and clean package recreation

**Files:**
- Create (ignored): `artifacts/publish/win-x64/**`
- Create (ignored): `artifacts/MarketplaceHub-win-x64.zip`

**Interfaces:**
- Consumes: cleaned source tree and Windows PowerShell-compatible packaging scripts
- Produces: verified self-contained Windows package

- [ ] **Step 1: Run the full Release test suite**

```powershell
dotnet test .\tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --nologo
```

Expected: all tests pass with zero failures.

- [ ] **Step 2: Check the tracked diff**

```powershell
git diff --check
```

Expected: no whitespace errors.

- [ ] **Step 3: Build and smoke-test the package**

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Package.ps1 `
  -OutputDirectory .\artifacts\publish\win-x64 `
  -PackagePath .\artifacts\MarketplaceHub-win-x64.zip
```

Expected: package smoke PASS, `MonoBridge — Yönetim Merkezi` MainWindow smoke PASS, and a package SHA-256 value.

- [ ] **Step 4: Open the verified package with real data**

Start `artifacts\publish\win-x64\TrMarketplaceHubDesktop.exe`, wait up to 30 seconds for a non-zero `MainWindowHandle`, and confirm the process responds.

- [ ] **Step 5: Report final size and retained candidates**

Report deleted byte totals, regenerated package size/hash, tests passed, visible window evidence, and any code/document candidates retained because safe deletion could not be proven.
