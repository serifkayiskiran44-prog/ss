# GitHub eski sürüm karşılaştırması — 2026-09-21

Karşılaştırılan uzak depo: `origin/main` (`2d7cb4c`). Karşılaştırılan yerel çalışma: `codex/bizimhesap-xml-integration` ve kaydedilmemiş güncel değişiklikler.

## Sonuç

- Eski GitHub sürümündeki kaynak dosyalardan hiçbiri yerel projede kayıp değil. `origin/main` ile karşılaştırmada silinmiş (`D`) dosya bulunmadı.
- Yerel sürüm GitHub sürümünün genişletilmiş hali: kayıtlı dal farkında 138 dosyada yaklaşık 11.487 ekleme ve 402 çıkarma var; sipariş/NES çalışması henüz commit edilmediği için bu sayıya ayrıca dahildir.
- Eski özelliklerin önemli bölümü derlemeye giriyor fakat sadeleştirilen sol menüde görünmüyor. Bu nedenle “kod yok” değil, “ekran yolu kapalı” durumu var.

## Geri kazandırılması yararlı olanlar

1. **Veri kalite merkezi** — eksik VKN/TCKN, e-posta, barkod, fiyat, stok ve zorunlu ürün alanlarını tek kuyrukta göstermek için yararlı.
2. **API bağlantı sağlığı + tanılama** — NES/Trendyol/Etsy/BizimHesap son bağlantı sonucu, HTTP hata sınıfı, kota ve güvenli audit özetini tek ekranda toplamak için yararlı.
3. **Kanal yayın matrisi** — bir yerel ürünün hangi mağaza/pazaryerine bağlandığını ve hangilerinde bağsız kaldığını göstermek için doğrudan kullanışlı.
4. **Stok/fiyat politika merkezi** — gerçek depo, sanal depo, güvenlik stoğu, kanal üst sınırı ve fiyat kuralını mağaza bazında yönetmek için yararlı.
5. **Sipariş istisnaları** — iptal, iade, eşleşmeyen SKU ve stok geri koyma kararlarını ayrı işlem kuyruğunda toplamak için yararlı.
6. **Görsel/medya merkezi** — ürün görsellerinin sırası, ana görseli ve bozuk bağlantılarını yönetmek için yararlı; mevcut ürün kartıyla birleştirilmesi daha sade olur.

## Mevcut haliyle geri getirilmemesi gerekenler

- Bağlantısı tamamlanmamış Amazon, Wish, Fruugo, Joom ve benzeri ekranlar: çoğu eski sürümde yerel plan/başvuru ekranıydı, çalışan canlı entegrasyon değildi.
- Mesaj merkezi: pazaryeri müşteri mesaj API’lerine bağlı olmadan yalnız yerel kayıt görünümü sağlar.
- Eski genel bakış ve veri geçiş asistanı: mevcut ürün, XML ve Excel ekranlarının işlevlerini tekrar ediyor ve arayüzü yeniden kalabalıklaştırır.
- Eski ayrı sync/otomasyon sayfaları: güncel XML otomasyonu ve pazaryeri çalışma alanlarıyla aynı işi iki yerde gösterir.

## Teknik not

`ScreenParityAudit.RequiredRoutes` hâlâ eski geniş menüyü zorunlu sayıyor. Güncel sade menü bu yolları kaydetmediği için başlangıç günlüğünde yanlış `BLOCKED` mesajı oluşabilir. Denetim sözleşmesi yeni menüye göre güncellenmeli veya geri getirilecek araçlar tek bir **Araçlar ve kontroller** ekranında açılmalıdır.

Önerilen sıra: veri kalite → API sağlık/tanılama → kanal yayın matrisi → stok/fiyat politikaları → sipariş istisnaları. Bu sıra mevcut iş akışındaki gerçek hataları görünür kılar ve ana menüyü yeniden kalabalıklaştırmaz.
