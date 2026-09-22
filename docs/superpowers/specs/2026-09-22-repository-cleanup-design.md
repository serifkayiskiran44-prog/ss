# MonoBridge Repository Cleanup Design

## Goal

Reduce repository and workspace clutter without changing the application's business behavior, deleting user data, or losing in-progress integration work. The cleanup must leave a reproducible Windows package and a visibly opening desktop application.

## Scope

The cleanup has three workstreams:

1. Remove reproducible build and release output.
2. Remove only code and documentation proven to be obsolete or duplicated.
3. Harden ignore rules and verify the application from a clean build.

The pending Trendyol category, brand, and match-candidate interface remains a separate feature. Cleanup must preserve its current code and tests so that feature work can continue afterward.

## Generated Output Cleanup

The following untracked or ignored paths are reproducible and may be removed:

- `bin/`
- `obj/`
- `artifacts/`
- root `Windows-*` publish directories
- root release ZIP files such as `MarketplaceHub-NES-win-x64.zip`
- build output under `tests/**/bin` and `tests/**/obj`

After cleanup, one fresh verified package may be recreated under `artifacts/`. It remains untracked.

The `.gitignore` file will explicitly include `artifacts/` so future package builds do not pollute `git status`.

## Protected Data and Work

The cleanup must not delete or reset:

- `%LOCALAPPDATA%\MonoBridgeDesktop` databases, logs, credentials, or marketplace settings
- `.worktrees/` or any registered Git worktree
- modified source files already present in the working tree
- NES, Trendyol, Etsy, BizimHesap, XML, Excel, order, stock, or invoice integration code that is reachable from the application
- tests that cover production behavior
- current plans/specifications that describe unfinished work

No database migration, credential reset, or marketplace API mutation is part of this cleanup.

## Code Cleanup Rules

Code may be removed only when all of the following are true:

- It has no production reference, XAML reference, reflection-based registration, test dependency, or documented runtime entry point.
- Removing it does not remove a visible navigation item or supported connector.
- The full test suite and Release build still pass afterward.

Large files are not considered unnecessary merely because they are large. Connector placeholders and compatibility code are retained unless their removal is proven safe. Existing uncommitted edits are reviewed but not overwritten.

Small, behavior-preserving cleanup is allowed when directly supported by evidence, including duplicate helper code, stale comments, obsolete one-off audit helpers, and documentation that repeats the same completed checklist. Refactoring unrelated working modules is outside scope.

## Documentation Cleanup

`TODO.md` and `IMPLEMENTATION_STATUS.md` may be shortened only by removing exact duplicate or obsolete generated history. Active requirements, unfinished items, operational instructions, and references needed for current integrations remain intact.

New or untracked order-workspace design documents are preserved because they describe current work.

## Verification

The completed cleanup must pass these checks in order:

1. `git diff --check`
2. Full Release test suite
3. Release build
4. `installer/Build-Package.ps1` on Windows PowerShell 5.1
5. Package manifest verification
6. Smoke test that observes the `MonoBridge — Yönetim Merkezi` window
7. Final process check confirming the opened application responds

If a deletion causes a failure, the deletion is reverted rather than weakening the test or startup gate.

## Deliverables

- Smaller workspace with reproducible outputs removed
- Updated ignore rules
- Evidence-backed code/document cleanup, if safe candidates are found
- Fresh verified Windows package
- A concise cleanup report listing removed paths, retained risky candidates, test results, and remaining disk usage
