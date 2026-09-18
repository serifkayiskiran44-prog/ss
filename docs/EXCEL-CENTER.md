# Excel aktarım merkezi

## İncelenen Entegra eğitimleri

Beş eğitimin tam Türkçe altyazısı incelendi; ortak akış dosya yükleme → sütun harflerini eşleme → işlem seçimi → satır önizlemesi → onaylı aktarım olarak uygulandı.

- [Ürün ekleme](https://entegrasyon.com.tr/?arama/iceri-excel-ekle/entegra-excel-urun-ekle): SKU anahtarı, yeni ürün ekle/güncelle, A/B/C eşleme, KDV, pazaryeri satış/liste fiyatları. Video: CgazvcUaFMk.
- [Kategori ekleme](https://entegrasyon.com.tr/?arama/kategori/entegra-excel-kategori-ekleme): tek sütunda Giyim > Erkek > Tişört yolu ve üst kategorilerin oluşturulması. Video: 0d84NkBoZpQ.
- [Stok/adet güncelleme](https://entegrasyon.com.tr/?arama/iceri-excel-ekle/entegra-excel-urun-adet-guncelle): SKU + adet, değiştirme veya mevcut adede ekleme. Video: JzXgIW0ughc.
- [Fiyat güncelleme](https://entegrasyon.com.tr/?arama/iceri-excel-ekle/entegra-excel-urun-fiyat-guncelle): SKU + fiyatlar, pazaryeri sütunları, KDV, bilinmeyen SKU atlama. Video: f762oYvPzT4.
- [Sipariş ekleme](https://entegrasyon.com.tr/?arama/iceri-excel-ekle/entegra-excel-siparis-ekleme): sipariş numarası, müşteri, fatura/sevk, ürün satırları, adet, vergi ve fiyat. Video: 8tupSMVuiaQ.

## Kullanım

1. Excel işlemleri menüsünde Ürün / Fiyat / Stok, Kategori Excel veya Sipariş Excel sekmesini seçin.
2. XLSX dosyasını yükleyin; sayfa ve başlık satırını belirleyin. Sütunları A, B, C … AA harfleriyle seçin veya yazın. Listede başlık ve örnek değer de gösterilir. Eşlemeleri profil olarak kaydedebilirsiniz.
3. Ürünlerde yeni ekle + güncelle, yalnız yeni ekle, mevcut ürünü güncelle, yalnız fiyat veya yalnız stok seçin. SKU zorunludur; Ürün ID ek tutarlılık kontrolüdür. Barkod başka ürüne çakışamaz, SKU yerine sessiz alternatif anahtar olmaz.
4. Güncellenecek alanın sütun harfini girin; alan tikleri yoktur. Dolu hücreler güncellenir; eşlenmeyen, boş ve ilgili XML kilidi etkin alanlar korunur. Sayısal sıfır geçerli bir güncellemedir. KDV hariç fiyatlar ürün KDV oranıyla dâhile çevrilir. Pazaryeri satış/liste fiyatları bağımsız eşlenebilir; aynı Excel sütunu birden fazla alana atanabilir.
5. Önizleme oluşturun. Gerçek Excel satır numarasıyla Eklenecek / Güncellenecek / Atlanacak / Hatalı durumlarını ve değişiklikleri inceleyin. Hata varsa önce dosyayı düzeltin; hatalı planın hiçbir satırı yazılmaz.
6. Geçerli satırları seçin ve uygulayın. Dosya, ayar veya veriler değişirse eski önizleme reddedilir. Siparişin bütün satırları birlikte seçilir.
7. Son işlemi geri al düğmesi yeniden açılıştan sonra da çalışır. İşlemden sonra veriler değişmişse geri alma engellenir; sonraki düzenlemeler ezilmez.

Kategori aktarımı üst düğümleri otomatik oluşturur. Sipariş aktarımı sipariş numarası + SKU ile gruplar; seçilmeyen müşteri, adres, toplam ve diğer ürün satırlarını korur. Yeni sipariş için ürün adı, pozitif adet ve fiyat veya toplam gerekir. Mevcut sipariş toplamı yalnız açık toplam eşlemesiyle değişir. Karma KDV hariç açık toplam ürünlerin net toplamıyla uyuşmuyorsa, indirim/kargo varsayımı yapılmaz; KDV dâhil aktarım istenir.

Siparişler sol menüdedir; sekmeye dönünce yerel kayıtlar yenilenir. Excel aktarımı yerel katalog/sipariş verisini değiştirir; pazaryerine yayın yapmaz ve sipariş stoğunu otomatik düşmez. Stok düşümü mevcut Siparişler ekranında ayrı işlemdir.

## Doğrulama

Geçici SQLite veritabanları ve gerçek XLSX dosyalarıyla SKU eşleşmesi, fiziksel sütun/satır, modlar, KDV, pazaryeri para birimi, XML kilitleri, seçilmeyen alanlar, barkod çakışması, atomik hata engeli, stale önizleme, yanlış veri klasörü, kalıcı geri alma, kategori ağacı, sipariş gruplaması ve WPF profil/menü yenilemesi test edilir.

## Genişletilmiş ürün alanları

Excel eşlemesine desi, ağırlık, en/boy/derinlik, menşei, GTİP, paket tipi, miad, ikinci alt başlık, marka/kategori ID bilgileri ve alış para birimi eklendi. Görsel 1–9 ve kategori seviyesi 2–5 ayrı sütunlardan güncellenebilir; boş hücreler mevcut değerleri korur. Ürün güncellemelerinde barkod zorunlu değildir. Siparişlerde mevcut ürünün adedi boşsa korunur; yeni ürün satırında pozitif adet gerekir.
