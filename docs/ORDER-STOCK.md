# Siparişten merkezi stok düşümü — .NET 8

Sipariş ve kargo ekranında kayıtlı siparişi seçin. Merkezi stok bölümünde SKU bazlı mevcut/düşülecek/kalan miktarlar görünür. Kaydedilmiş siparişi stoktan düş düğmesi yerel kataloğu değiştirir. Düzenlenmiş fakat kaydedilmemiş satırlar kullanılmaz.

Pazaryeri + mağaza + sipariş numarası idempotency anahtarıdır. Aynı SKU toplamlarıyla tekrar çağrı önceki sonucu verir. Satır sırası/başlık değişikliği aynı işlemdir; SKU/adet değişikliği tekrar düşmez, çakışma verir. SKU tam eşleşmeli ve tek merkezi ürüne ait olmalıdır. Kaynaklar arası aynı SKU belirsizdir.

catalog.db içinde Product JSON değişikliği, OrderStockReceipts ve OrderStockMovements aynı SQLite BEGIN IMMEDIATE transaction içindedir. İkinci satır hatası tüm işlemi geri alır; eşzamanlı yazıcılar seri çalışır. Negatif stok yazılmaz. Product UpdatedUtc eski düzenleme ekranının stok düşümünü ezmesini önler.

Düşülen üründe LockStock=true yapılır. Bu, XML'in tedarikçi stoğunu tekrar yazarak yerel düşümü geri almasını önler. XML fiyat/açıklama gibi diğer izinli güncellemeler sürer. Stok kilidini kullanıcı kaldırırsa XML yine stok yazabilir. Merkezi depo/rezervasyon/safetyStock motoru henüz tamamlanmış değildir.

Geçmiş siparişler otomatik işlenmez: önceden elle düşülmüş stok tekrar düşürülmemelidir. Sipariş alımı salt okunur kalır. Harici kanal güncellemesi, otomatik iptal/iade stok iadesi ve kuyruk bu modülün tamamlanmış işlevleri değildir. Para/kargo durumu stok kararı için otomatik yorumlanmaz.

Kanıt: 15 yeni test; tam Release234/234. Duplicate, changed replay, sıra bağımsızlık, iki satır rollback, eşzamanlı oversell, pasif/eksik/çakışan SKU, overflow, XML koruma, stale editor. Release win-x64 self-contained publish başarılı. WPF görsel kullanıcı testi bu aşamada yapılmadı.
