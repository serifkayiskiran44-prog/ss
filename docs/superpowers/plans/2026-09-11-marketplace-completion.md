# MonoBridge Marketplace Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Complete the remaining in-scope marketplace operations in the .NET 8 WPF desktop application, with local atomic behavior, tests, documentation, and self-contained Windows publication for each milestone.

**Architecture:** Keep the shared `CatalogStore` SQLite transaction boundary as the source of truth. Add focused stores/services for mappings, sync jobs, errors, schedules, shipping, returns, and reports; UI panels call these local services and never claim unsupported live APIs. Marketplace connectors are capability-based adapters, with official API contracts required before live writes.

**Tech Stack:** .NET 8 WPF, C#, SQLite, ClosedXML, xUnit, self-contained `win-x64` publish.

**Spec:** `PROJECT_SPEC.md`, `TODO.md`, `docs/ENTEGRA3-ANALYSIS.md`, `docs/ENTEGRA3-ARCHITECTURE.md`.

## Global Constraints

- No login or application password; preserve encrypted API credentials.
- Do not implement deferred areas: variants, bundles, inline editing, critical price, XML variant mapping, fulfillment core, settlement core.
- Do not invent marketplace endpoints; live writes require official API contracts and explicit preview confirmation.
- All catalog/order stock mutations are atomic and use temporary test databases.
- Every milestone requires focused tests, full Release tests, TODO/status updates, and a self-contained EXE publish.

### Task 1: Excel mapping and rollback

Implement a visible manual column-mapping dialog, selected-row apply, import batch identity, error report export, and reversible catalog import journal. Test missing/duplicate mappings, selected rows, rejected rows, rollback, and optimistic conflicts.

### Task 2: Category, brand, and attribute core

Add normalized local category/brand/attribute stores, CRUD validation, product references, mapping tables, preview, and migration-safe serialization. Add WPF management and mapping panels.

### Task 3: Sync center and error retry

Add durable sync jobs, idempotency keys, status/error records, retry policy, cancellation, and a local preview-only dispatcher. Expose queued/running/succeeded/failed states in WPF.

### Task 4: Automatic stock and price jobs

Add persisted schedules and a single-run lock, calculate channel payloads from stock and price policies, retain preview snapshots, and prevent duplicate dispatch. Keep live connector writes behind capabilities.

### Task 5: Etsy vertical expansion

Extend the existing official Etsy client for product, stock, price, and order read/write flows behind preview confirmation, rate/error handling, and idempotent local receipts.

### Task 6: Official marketplace connectors

Implement eBay, Ozon, and Joom only from verified official API contracts; add capability discovery and clear unsupported states. Add Allegro, Wish, and Fruugo adapters only where official credentials/contracts are available.

### Task 7: Shipping, tracking, cancellation, and returns

Define shipping adapter contracts, local shipment records, tracking submission preview, cancellation/refund decisions, and atomic stock restoration with duplicate protection.

### Task 8: Reports and release hardening

Add operational reports, error/export summaries, visual WPF smoke checks, documentation, full test suite, and final self-contained publication. Record unsupported capabilities and remaining non-goals.
