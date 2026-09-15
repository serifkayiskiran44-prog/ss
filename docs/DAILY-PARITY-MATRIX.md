# Günlük kullanım parite matrisi (M46)

| Günlük başlık | Durum | Programdaki karşılık |
|---|---|---|
| Ürünler listesi / genel kullanım | TAM | Ürün yönetimi, sayfalama, kolon arama, ürün havuzu |
| Toplu işlemler | TAM | Toplu ürün işlemleri; preview, stale kontrolü, açık onay ve iptal |
| Detaylı arama / filtreleme | TAM | Ürün, sipariş, XML, sync, kalite ve global Ctrl+K araması |
| Sağ tık / toplu işlem karşılığı | TAM | Özgün seçili satır + işlem paneli; canlı write yok |
| Kategoriler | TAM | Kategori/marka/özellik sözlük ve mapping merkezi |
| Markalar | TAM | Yerel marka sözlüğü ve mağaza/kanal eşlemesi |
| XML işlemleri | TAM | Kaynak, test, mapping, önizleme, import geçmişi ve otomasyon |
| Ayarlar | TAM | Mağaza bağlantıları, döviz/KDV, stok/fiyat ve yedek |
| Excel işlemleri | TAM | Profil, kolon eşleme, preview, create/update/skip/error, dışa aktarma |
| Sipariş listesi | TAM | Sipariş/kargo, filtre, duplicate-safe stok kararı ve exception merkezi |

## Güvenlik ve kapsam notu

Toplu veya riskli işlemler preview + explicit approval + stale/idempotency kapılarından geçer. Büyük listeler sanallaştırılır/sayfalanır; uzun işlemler iptal edilebilir. Varyant, bundle/set, hızlı düzenleme, kritik fiyat, XML varyant mapping, fulfillment ve settlement/reconciliation `DEFERRED_BY_USER` kalır. Doğrulanmamış marketplace write açılmaz.
