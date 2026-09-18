# Trendyol control workspace implementation plan

**Goal:** Entegra eğitimlerindeki ayrı ayarlar formunu, geniş ürün kontrol listesini ve ürün kartı akışını MonoBridge'e uyarlamak.

**Architecture:** Mevcut WPF paneli ve immutable gönderim motoru korunur. Kontrol, ayarlar, rekabet ve işlem geçmişi ana bölümlerdir. Ürün kartı ve önizleme listeyi daraltan sabit paneller yerine ayrı görünüm olarak açılır.

**Reference:** Entegra setup `g1JefYlvciE` (04:38), listing `sldBPpBuWOM` (01:01, 01:32, 02:02). İki sütunlu ürün kartı, kategori özellikleri ve ayrı yayın/işlem durumları. Güncel resmi Trendyol onaylı/onaysız V2 belgeleri esas alınır.

**Constraints:** .NET 8 WPF, mevcut veri ve şifreli kimlik bilgileri korunur. Yeni otomasyon veya kullanıcıca ertelenmiş modül eklenmez. Canlı deneme yalnız PTD-1367; onay bekleyen ürüne stok/fiyat gönderilmez. Testler geçici katalog kullanır.

- [x] API durum kodları, red gerekçeleri ve eski önbellek uyumluluğu: alt görev API dosyalarını testlerle günceller.
- [x] Önce UI regresyon testi: başlangıç kontrol görünümü, ayarlara doğrudan erişim, tüm katalogda durum filtresi, seçim değişince önizleme iptali.
- [x] Kontrol listesini genişlet; durum sayaçları, arama, kategori/marka durumları ve tek işlem çubuğu. Ürün kartı / önizleme ayrı görünüm.
- [x] Ayarları bağlantı, kategori/marka ve teslimat olarak grupla; uygulama Ayarlar bağlantısını doğrudan bağlantı formuna aç.
- [x] Teslimat şablonu kaydet/düzenle kimliği, seçili ürün ve API yenilemesinde taslak koruması; kaydet/yeni/stale/eksik adres regresyon testleri.
- [x] 1038 Release testi; belirtilen Windows-Excel-Final yayını; 1920 ve 1426 genişlikte gerçek GUI kontrolü, tek ürün teslimat/adres gönderimlerinde SUCCESS.
- [x] Aynı canlı ürünün onay/yayın durumunu kontrol et; oluşturma, fiyat, stok, birleşik güncelleme ve GUI teslimat/adres sonuçlarını belgelerde kaydet.

Release branch: `codex/bizimhesap-xml-integration`. Commit/push sonucu görev yanıtında ve Git geçmişinde izlenir.

Ruling: UI düzeni kullanıcı tarafından açıkça istendiği için yeniden tasarım onayı beklenmeden uygulanır. Entegra'nın eski API seçenekleri aynen kopyalanmaz; yalnız mevcut güncel servislerle çalışan işlemler gösterilir.
