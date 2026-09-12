# #284 Import Repair Implementation Plan

**Goal:** Close all eight audit findings on the existing `codex/issue-284-import-repair` branch without changing marketplace production data.

**Architecture:** Persist XML mapping/profile revision and feed health in the existing SQLite stores; run XML applies through a lease-backed idempotent run record. Move Excel preview/apply through cancellable UI commands and deterministic culture parsing. Verify each path with real temporary SQLite, XML, XLSX, WPF entry-point, and CI-compatible Release tests.

**Tech Stack:** .NET 8 WPF, SQLite, ClosedXML, MSTest, GitHub Actions.

**Spec:** GitHub Issue #284 and current AGENTS.md/PROJECT_SPEC.md.

## Global Constraints

- Remain on `codex/issue-284-import-repair`; do not create or switch to another issue branch.
- Do not touch DEFERRED_BY_USER areas or write to live marketplaces.
- Preserve LocalAppData and encrypted credentials.
- Use preview, stale, idempotency, and explicit approval gates for any live operation.
- Required verification: Release tests and self-contained `win-x64` publish.

## Tasks

1. Add persisted XML mapping/profile revision and feed completeness metadata; reject stale namespace/path/repeat mappings before manual and scheduled apply.
2. Persist source-missing quarantine cases and integrate them into CatalogStore import and source-health UI without destructive deletion during grace.
3. Add an async, cancellable, single-entry Excel apply controller to the real WPF panel, with hash/profile/product revision receipt validation.
4. Replace implicit CurrentCulture/invariant fallbacks with explicit profile culture and structured parser rejection reasons for XML, Excel, and migration input.
5. Add XML run lease/heartbeat/recovery and feed-hash idempotency while keeping the existing one-running database backstop.
6. Add real production-chain performance fixtures and bounded metrics for XML and XLSX paths.
7. Replace helper-only coverage with integration tests through the reader/parser/store and WPF command entry points.
8. Fix the Windows Release Evidence restore/publish path and validate the same SHA; preserve LIVE_API_BLOCKED behavior.
