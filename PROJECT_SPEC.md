# MonoBridge Desktop — güncel proje kararı

2026-09-11: Kullanıcı .NET 8 WPF masaüstü EXE üzerinde devam edilmesini, önceki kapsamda kalan çalışmaların buraya uyarlanmasını istedi. Programa giriş parolası, login veya kullanıcı hesabı ekranı eklenmeyecek. API hesaplarının mevcut şifreli saklanması korunur; bu uygulama giriş şifresi değildir.

Mevcut kod temel alınır. CommerceHub silinmez; davranış/test/doküman referansıdır. Web kodu veya PostgreSQL verisi otomatik taşındı sayılmaz. Önceki kapsam kaynağı ../CommerceHub/PROJECT_SPEC.md ve docs/18-CORE-160-REQUEST.md; teknoloji/auth maddelerini bu karar geçersiz kılar.

Varyant sistemi, bundle/paket/set, hızlı satır içi düzenleme, kritik fiyat, XML varyant mapping, fulfillment core, hakediş/mutabakat core DEFERRED_BY_USER. Alt ekran/test/özellikleri yeni kapsam veya eksik olarak sayılmaz. Mevcut çalışan davranış korunur.

Ürün → stok → fiyat → kategori/marka → filtre/toplu işlemler → Excel/XML → sipariş/kargo/sync; ardından Etsy öncelikli gerçek connector geliştirmesi. Var olan modül tekrar yazılmaz. Canlı API yazımı somut önizleme onayı gerektirir. Mevcut 160 transkript referanstır; yeni transkript toplama durdurulmuştur.

Veriler LocalAppData/MonoBridgeDesktop içinde kalır. Yeni yayın dizini Windows-Current; .NET çalışma zamanını yanında içerir. EXE bu klasörün diğer dosyalarıyla birlikte tutulmalıdır.

En son yayın: Windows-Operations/TrMarketplaceHubDesktop.exe. Windows-Current önceki çalışan yayın olarak korunur.

Güncel yayın: Windows-Stock/TrMarketplaceHubDesktop.exe (sipariş stok işlemleri dahil). Eski yayınlar korunur.

Güncel yayın: Windows-Policies/TrMarketplaceHubDesktop.exe. Önceki sürümler korunur.

Güncel yayın: Windows-Price/TrMarketplaceHubDesktop.exe; mağaza fiyat politikası önizlemesi dahil.
