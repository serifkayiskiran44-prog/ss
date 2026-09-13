# XML kullanım paritesi kabul notu

M43 kapsamında doğrulanan özgün akış:

1. **XML yönetimi** ekranından kaynak adresi, format, aralık, kültür ve fiyat/stok kuralları tanımlanır.
2. Kaynak okunur; HTTP durum kodu, timeout, gzip, UTF-8 ve güvenli XML parser kontrolleri uygulanır.
3. Ürün düğümü ve alan yolları incelenir; SKU/barkod/ad/açıklama/marka/kategori/stok/fiyat/döviz/KDV/görsel/GTIN alanları eşlenir.
4. Alan eşlemeleri kaynak profiline kaydedilir. Önizleme olmadan katalog yazımı başlatılmaz.
5. Önizleme `CREATE`, `UPDATE`, `SKIP` ve `ERROR` kararları ile duplicate SKU/barkod ve zorunlu alan hatalarını gösterir.
6. Onay sonrası transaction içinde yalnız değişen kayıtlar işlenir; import geçmişi, sayaçlar ve hata özeti saklanır.
7. Otomasyon ekranındaki XML şablonu son çalışma, retry/backoff ve durum geçmişini gösterir.

Sipariş kaynaklı yerel stok düşümü XML yenilemesinde korunur (`LockStock`); tedarikçi stoğu sipariş hareketini geri yazmaz. XML varyant mapping kullanıcı kapsamı gereği `DEFERRED_BY_USER` kalır. Canlı marketplace write ve doğrulanmamış endpoint yoktur.

## UX fark listesi

- Teknik alan yolları ve XPath yerine mevcut panelde önerilen alan adları görünür; ileri kullanıcı için kaynak yolu saklanır.
- Önizleme ve onay ayrımı zorunludur; doğrudan toplu yazma düğmesi bulunmaz.
- Her çalışma için create/update/skip/error sayıları ve son hata görünür.
- Mapping kopyalama mevcut profil/ayar kopyalama davranışını kullanır; credential kopyalanmaz.
