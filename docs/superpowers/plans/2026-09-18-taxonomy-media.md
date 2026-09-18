# Category, Brand and Product Media Implementation Plan

> Execute sequentially in this task using executing-plans. Orders redesign was explicitly removed by the user.

**Goal:** Expose category/brand management below products, reproduce the supplied training workflows locally, and own product image copies with safe product deletion.

**Architecture:** Keep catalog.db as product/taxonomy authority; reuse TaxonomyStore for entries and scoped mappings. Add a focused tree workspace and scoped content templates. Keep remote source URLs; cached image copies live below the application data directory and are owned by product ID.

**Tech Stack:** .NET 8, WPF, SQLite, ClosedXML; temporary test stores only.

**Spec:** User instructions and docs/EXCEL-CENTER.md. Training evidence: qExr5_z46xs, o2zEDMSfA4w, 0d84NkBoZpQ, wHxbcUlmb8c, T5eAwHIuTDQ, tU7CH7bbtKw (full Turkish transcripts read).

## Constraints
- Preserve all existing work and credentials.
- Do not implement deferred variants/bundles/fulfillment/reconciliation.
- No live marketplace writes or fabricated remote category catalogs.
- Empty Excel cells preserve values; SKU remains identity; barcode optional.
- Do not delete external source files or another product's files.

## Task 1 — Excel completion
- [x] Remove per-field checkboxes; capture every nonempty mapping.
- [x] Extend actual supported product fields, retain locks/blank behavior and existing order blank quantity.
- [x] Add regression tests; 892 Release tests pass.
- [x] Publish Windows-Excel-Final.

## Task 2 — Categories and brands
Files: Catalog/TaxonomyWorkspace.cs, TaxonomyWorkspacePanel.cs, MainWindow.Navigation.cs; tests/MarketplaceHub.Tests/TaxonomyWorkspaceTests.cs.
- [x] Test parent/child creation, path rename updating descendants/products atomically, product counts and duplicate/cycle rejection.
- [x] Implement category path hierarchy and brand rename without changing IDs; reload after Excel imports.
- [x] Build separate category/brand pages below Products with search, tree/list, counts, add/edit/delete, scoped mappings and category Excel access.
- [x] Test and expose scoped title/description templates with original-field placeholders and required/default attributes; previews preserve base product and XML locks.

## Task 3 — Product-owned image copies and deletion
Files: Catalog/ProductAssetCache.cs, Catalog/CatalogStore.ProductDeletion.cs, MainWindow.ProductCard.cs, MainWindow.ProductList.cs, Catalog/Models.cs; tests/MarketplaceHub.Tests/ProductAssetCacheTests.cs.
- [x] Test saving validated bounded image bytes under product-owned paths; preserve remote URLs and external files.
- [x] Integrate background synchronization for loaded catalog records, thumbnail fallback and visible storage status.
- [x] Make deletion remove product-owned media/cache records and files, with path/reparse checks and retryable cleanup; block removal of published products as existing rules require.
- [x] Add Desi read-only product list column and verify editable product-card field.

## Task 4 — Verification
- [x] Run dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release.
- [x] Publish self-contained win-x64 to Windows-Excel-Final and open the application.
- [x] Verify WPF navigation/category/brand/Excel screens and update TODO / IMPLEMENTATION_STATUS.

Validation: 899 Release tests passed, 0 failed, 0 skipped. Native WPF category navigation and layout inspected. Final publish and launch evidence is in tools/workspace-final-publish.log and application process verification.

## Kullanıcı düzeltmesi: eğitimdeki liste ve toplu işlemler
- [x] Kategori/marka ağaç-form görünümü yerine çoklu seçimli ana liste.
- [x] Toplu sil/aktif/pasif, kategoriye bağla, onar, ürün say, Excel aktar.
- [x] Pazaryeri sütunları ve hızlı hücre düzenleme; atomik toplu kayıt.
- [x] Seçilenlere/tümüne atomik şablon kopyalama.
- [ ] Son tam test, yayın ve uygulama içi kontrol.
