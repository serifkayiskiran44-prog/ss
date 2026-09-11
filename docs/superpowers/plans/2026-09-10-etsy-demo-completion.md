# Etsy Demo Completion Implementation Plan

> **For agentic workers:** Execute this bounded integration directly using executing-plans, preserving the existing draft duplicate guard.

**Goal:** Create a desktop Etsy draft with its first product image and retain the draft ID if image upload fails.

**Architecture:** Prepare the first pipe-separated product image before sending a draft. Persist the creation attempt before POST and the returned listing ID before uploading the image. Never retry either write automatically.

**Tech Stack:** .NET 8, WPF, HttpClient, xUnit.

**Spec:** User-authorized desktop completion and sample Etsy draft; no publishing.

## Constraints

- Preserve existing credentials and duplicate protection.
- Support first image only; disclose pending video, extra images and SKU/variant inventory.
- Build into a new Windows-Etsy-Deneme folder without modifying the running package.

## Implementation

- [x] Add a testable image/draft sequence to EtsyDrafts.cs and focused ordering/failure tests in EtsyImageTests.cs.
- [x] Wire MainWindow.xaml.cs to the sequence and update status/help text.
- [x] Run the full existing xUnit suite and WPF build.
- [x] Publish a self-contained Windows package into Windows-Etsy-Deneme.

**Interfaces:** PrepareImageAsync(string, CancellationToken) returns image bytes. UploadImageAsync(EtsyCredentials, long, byte[], CancellationToken) returns image ID. CreateAsync retains its pre-POST persistence callback.

Validation: 161 tests passed, 0 failed; Release WPF build 0 warnings/errors; win-x64 self-contained single-file publish completed into Windows-Etsy-Deneme. No live requests were used for these checks.
