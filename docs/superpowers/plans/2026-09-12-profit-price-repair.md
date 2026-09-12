# Plan: Profit and price repair

## Goal
Unify price preview and dispatch safety around an explicit cost/expense/FX snapshot contract.

## Steps
1. Add red integration tests for expense-aware margin protection, unknown inputs, FX provenance, and dispatch preflight.
2. Implement the shared money contract and deterministic calculator.
3. Route local preview/automation/dispatch preflight through the calculator without performing live writes.
4. Add cache/snapshot and stale approval guards, then UI-safe diagnostics.
5. Run targeted and full Release verification, publish self-contained win-x64, and record evidence.

## Verification
- Targeted profit/price integration tests
- Full Release test suite
- Release build and self-contained win-x64 publish
- `git diff --check`
