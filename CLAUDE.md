# CLAUDE.md

Bu dosya, Claude Code'un bu repo (MarketplaceHub / TrMarketplaceHubDesktop) üzerinde çalışırken izlemesi gereken kuralları özetler. Önce bu dosyayı, sonra `AGENTS.md`, `PROJECT_SPEC.md` ve `TODO.md` dosyalarını oku.

## Proje özeti

**MarketplaceHub (TrMarketplaceHubDesktop)** — .NET 8 WPF masaüstü uygulaması (Windows, self-contained EXE). Türkiye ve global pazaryerleri için ürün/stok/fiyat/sipariş/senkronizasyon yönetimi yapan bir "marketplace hub" masaüstü aracı.

- **Platform:** .NET 8, WPF (`net8.0-windows`), `TrMarketplaceHubDesktop.csproj`
- **Veri katmanı:** SQLite (`Microsoft.Data.Sqlite`), her modülün kendi `.db` dosyası (catalog, sync, orders, media, messages, data-quality, api-health, automation, audit, vb.) — hepsi `%LocalAppData%\MonoBridgeDesktop` altında
- **Excel:** ClosedXML ile XLSX import/export
- **Kimlik/credential:** DPAPI ile şifreli yerel saklama (`CredentialStore.cs`). **Uygulamada login/parola ekranı YOK ve eklenmeyecek** — bu kesin bir kullanıcı kararı. API hesap bilgileri (pazaryeri credential'ları) ayrı, şifreli saklanır; bu, uygulamaya giriş şifresi değildir.
- **Test:** `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release`
- **Yayın:** `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o <Windows-XXX>`
- **CommerceHub:** Önceki (web tabanlı) kapsamın kod tabanı; bu repoda yok ama davranış/test/doküman referansı olarak anılır. Web kodu veya PostgreSQL verisi bu repoya otomatik taşınmış sayılmaz.

### Desteklenen/entegre kanallar
Etsy, eBay, Amazon, Trendyol, Hepsiburada, Ozon, Allegro, Joom, Wish, Fruugo, Navlungo. Çoğu kanal için resmi API sözleşmesi/credential doğrulanmadığı sürece canlı yazma **`LIVE_API_BLOCKED`** olarak açıkça işaretlenir — endpoint uydurulmaz, sahte HTTP çağrısı yapılmaz.

### Ana modüller (WPF panelleri)
Ürün yönetimi, toplu ürün işlemleri, XML tedarikçi merkezi, Excel içe/dışa aktarma, kategori/marka/özellik eşleme (taxonomy), sipariş merkezi + stok kararı, sipariş istisna/iptal/iade, sync & otomasyon merkezi, mağaza bağlantıları, API sağlığı, medya/görsel yönetimi, mesaj merkezi, veri kalitesi, dashboard, tanılama/audit/destek paketi, yedekleme/geri yükleme, onboarding, üretim hazırlığı (production readiness).

## Yeni özellik SAYILMAYAN / kapsam dışı (DEFERRED_BY_USER) alanlar

Aşağıdaki alanlar kullanıcı tarafından **açıkça ertelenmiştir**. Bunlarla ilgili yeni kod, ekran, model veya test **eklenmez**. Bir issue bu alanlara dokunuyor gibi görünüyorsa önce kullanıcıya sorulur / atlanır, "eksik" veya "yapılmadı" olarak raporlanmaz:

- **Paket / Set / Bundle** sistemi
- **Varyant Sistemi** (ürün varyantları)
- **Hızlı satır içi düzenleme** (inline quick-edit)
- **Kritik Fiyat** (critical price) mantığı
- **XML Variant Mapping** (XML varyant eşleme)
- **Fulfillment Core** (sevkiyat/lojistik çekirdeği)
- **Hakediş / Mutabakat Core** (settlement/reconciliation çekirdeği)

Bu yedi alanın alt ekran/test/özellik geliştirmeleri de kapsam dışıdır ve eksik sayılmaz. Mevcut çalışan davranış korunur, silinmez.

## Issue formatı

Bu repodaki issue'lar (Codex/otomasyon için hazırlanmış) şu alanları içerir — kod yazmadan önce hepsini oku:

```
CODEX_READY:true|false
TASK_MODE:REAL_DELIVERABLES|...

SOURCE/OWNER
- İlgili dosya/servis, production owner, mevcut doğrulanmış davranış

YAPILACAK İŞ / PRODUCTION_WORK
- Yapılması istenen değişikliğin somut tanımı

PRODUCTION ENTRY POINT
- Kullanıcı akışından ilgili servis/metoda giden gerçek çağrı zinciri

ACCEPTANCE / TEST (ACCEPTANCE_TEST)
- Kabul kriterleri; genelde test edilebilir madde listesi

EDGE CASES (EDGE_CASE)
- Sınır durumlar (bozuk veri, race condition, encoding, vb.)

SECURITY
- Secret/PII/credential sızıntısı olmaması, canlı marketplace write kısıtları

DEPENDS_ON
- Bu issue'nun bağımlı olduğu diğer issue/PR numaraları (varsa)
```

`SOURCE/OWNER` alanında bazen referans kod (ör. decompile edilmiş bir ZIP içindeki dosya/metot adları) belirtilebilir. Bu referans yalnızca **statik olarak incelenir** ve mevcut MarketplaceHub mimarisine uyarlanır:
- Referans kodun tamamı olduğu gibi kopyalanmaz; yalnızca davranışı doğrulanmış ve gerçekten yararlı kısım adapte edilir.
- PDB/decompile çıktısındaki dosya veya metot adları, çalışan bir özelliğin **kanıtı sayılmaz**.
- Bilinmeyen/kaynağı belirsiz EXE veya DLL **çalıştırılmaz**.

## Çalışma kuralları (özet — bkz. `AGENTS.md`)

1. Her değişiklikte ilgili anlamlı testler yazılır/çalıştırılır ve Release publish denenir. Testler geçici veri dizini kullanır; gerçek mağazada test yazması yapılmaz.
2. `TODO.md` ve `IMPLEMENTATION_STATUS.md` güncel tutulur; yerel/taslak kanal planları gerçek API olarak raporlanmaz.
3. **Canlı write güvenlik kapısı:** Gerçek pazaryerine yazma yalnızca immutable preview + açık kullanıcı onayı + stale/revision kontrolü + idempotency/receipt + wrong-store/account guard ile yapılır. Otomatik test asla gerçek mağazaya yazmaz.
4. Secret/PAT/token/password/API key repo, fixture, log veya issue içine yazılmaz; hata mesajları maskelenir (redaction).
5. **Codex GitHub çalışma kuralı:** Yerel `gh` oturumu veya token/credential istenmez — bu depoda zaten `gh` kimlik doğrulaması kuruludur. Kod/test/publish tamamlanınca ilgili branch commit edilip `git push` yapılır. `codex/issue-<numara>-<kisa-ad>` formatındaki branch push'ları `.github/workflows/codex-pr-router.yml` tarafından otomatik PR'a çevrilir.
6. Commit mesajlarında ilgili issue numarasına referans verilir (ör. `fix #2591: ...`).

## Öncelik sırası (issue çalışırken)

Açık issue'lar önce etiket önceliğine (P0 > P1 > P2 > P3, etiketsiz en son), sonra issue numarasına göre sıralanır. Bir issue'yu uygulamaya başlamadan önce `DEPENDS_ON` alanındaki bağımlılıkların tamamlanmış olup olmadığı kontrol edilir.
