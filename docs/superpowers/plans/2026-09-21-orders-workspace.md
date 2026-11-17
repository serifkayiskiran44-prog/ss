# Sipariş Çalışma Alanı Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sipariş listesini tam genişlikte çalışan, ayrı sipariş kartı, manuel/Excel aktarım, toplu işlem ve iade girişleri olan Türkçe masaüstü çalışma alanına dönüştürmek.

**Architecture:** Geriye uyumlu sipariş modeli ve saf toplam hesaplayıcı UI'dan ayrılır. `OrdersPanel` liste ve filtreleri yönetir; yeni `OrderCardWindow` görüntüleme/düzenleme işini, mevcut `ExcelWorkspacePanel` ise önizlemeli aktarımı yürütür.

**Tech Stack:** .NET 8, WPF, SQLite, System.Text.Json, ClosedXML, xUnit

**Spec:** `docs/superpowers/specs/2026-09-21-orders-workspace-design.md`

## Global Constraints

- Windows 11 ve PowerShell 5.1 üzerinde .NET 8 WPF EXE.
- Programa giriş veya parola ekranı eklenmez.
- Mevcut credential ve sipariş kayıtları korunur.
- Canlı pazaryeri yazımı immutable önizleme, açık onay, stale/revision, idempotency ve hesap korumaları olmadan çalışmaz.
- Kullanıcının ertelenen yedi alanına yeni model veya ekran eklenmez.

---

### Task 1: Sipariş veri sözleşmesi ve toplam hesabı

**Files:**
- Modify: `OrdersModels.cs`
- Create: `OrderTotals.cs`
- Test: `tests/MarketplaceHub.Tests/OrderTotalsTests.cs`

**Interfaces:**
- Produces: `OrderTotalsCalculator.Calculate(OrderSnapshot)` ve kart/Excel için geriye uyumlu alanlar.

- [ ] Net, KDV, brüt, indirim ve komisyon hesaplarının başarısız testlerini yaz.
- [ ] Testi çalıştırıp eksik sözleşmeyle başarısız olduğunu doğrula.
- [ ] Model alanlarını ve saf hesaplayıcıyı uygula.
- [ ] Hedefli testleri çalıştırıp geçtiğini doğrula.

### Task 2: Entegra Excel başlık profili

**Files:**
- Modify: `ExcelOrderImport.cs`
- Modify: `ExcelWorkspacePanel.cs`
- Test: `tests/MarketplaceHub.Tests/ExcelOrderImportTests.cs`

**Interfaces:**
- Consumes: Task 1 sipariş alanları ve toplam hesaplayıcı.
- Produces: 124 sütunlu dışa aktarım için otomatik alan önerisi ve güvenli import.

- [ ] Türkçe/mojibake başlık normalizasyonu ve örnek dosya eşleme testlerini yaz.
- [ ] Testlerin önce başarısız olduğunu doğrula.
- [ ] Kimlik, müşteri, fatura, sevk, kargo, ödeme, tutar, indirim, komisyon ve ürün alanlarını eşle.
- [ ] Önizleme, atomik uygulama ve geri alma testlerini çalıştır.

### Task 3: Ayrı ve geniş sipariş kartı

**Files:**
- Create: `OrderCardWindow.cs`
- Modify: `OrdersStore.cs`
- Test: `tests/MarketplaceHub.Tests/OrderCardWorkspaceTests.cs`

**Interfaces:**
- Consumes: `OrderTotalsCalculator.Calculate`.
- Produces: `OrderCardWindow.ShowExisting(...)` ve `OrderCardWindow.ShowNew(...)`.

- [ ] Kart görünüm modeli ve manuel sipariş doğrulama testlerini yaz.
- [ ] Testlerin önce başarısız olduğunu doğrula.
- [ ] Altı sekmeli kartı, ürün tablosunu, toplam kutusunu ve kaydetme akışını uygula.
- [ ] NES fatura düğmelerini mevcut `OrderInvoiceDialog` akışına bağla.
- [ ] Hedefli testleri çalıştır.

### Task 4: Tam genişlik sipariş listesi ve filtreler

**Files:**
- Modify: `OrdersPanel.cs`
- Test: `tests/MarketplaceHub.Tests/OrderFilterTests.cs`

**Interfaces:**
- Consumes: Task 3 kart penceresi.
- Produces: liste, ayrıntılı filtre paneli, çift tık kart açma ve toplu işlem menüsü.

- [ ] Yeni tarih/ödeme/kargo/işaret filtrelerinin saf predicate testlerini yaz.
- [ ] Sabit sağ paneli kaldır; tam genişlik grid ve açılır filtreleri uygula.
- [ ] Çift tık, `Sipariş kartını aç`, `Yeni sipariş`, `Excel’den sipariş al` ve `İadeler` eylemlerini bağla.
- [ ] Çoklu seçim menüsünü mevcut güvenli önizlemelere bağla.
- [ ] Hedefli testleri çalıştır.

### Task 5: İade bağlantısı, dokümantasyon ve yayın

**Files:**
- Modify: `OrderExceptionsPanel.cs`
- Modify: `TODO.md`
- Modify: `IMPLEMENTATION_STATUS.md`

**Interfaces:**
- Consumes: sipariş kartı ve mevcut yerel iade/stok geri koyma çekirdeği.
- Produces: özgün siparişe dönebilen birleşik iade kuyruğu.

- [ ] İade satırından sipariş kartına geçişi bağla ve yerel/canlı karar açıklamalarını netleştir.
- [ ] Sipariş çalışma alanı durumunu dokümanlara kaydet.
- [ ] `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release` çalıştır.
- [ ] `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Orders` çalıştır.
- [ ] Yayınlanan EXE'yi açıp liste, kart ve Excel girişlerinin görsel smoke kontrolünü yap.
