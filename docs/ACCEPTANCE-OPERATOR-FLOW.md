# Operatör akışı kabul senaryosu (Issue #891)

Paketlenmiş uygulama (`publish/win-x64/TrMarketplaceHubDesktop.exe`, kendi kendine yeten win-x64 Release yayını) üzerinde uçtan uca operatör akışının tasarım, durum, klavye, DPI ve onay davranışlarını doğrulayan kabul senaryosu. Aynı adımlar otomatik olarak `tests/MarketplaceHub.Tests/OperatorAcceptanceTests.cs` ile her CI çalışmasında koşar; bu belge paketlenmiş uygulamada elle tekrarlanacak halidir.

Kapsam dışı: bundle / varyant, satır içi düzenleme, Kritik Fiyat, XML varyant, Fulfillment ve Hakediş özellikleri bu senaryoda doğrulanmaz.

Güvenlik: senaryo hiçbir canlı pazaryerine yazmaz. Yazma senaryosu geçtiği yerde yalnız önizlemenin ve koruyucuların (onay, salt okunur mağaza, yanlış mağaza reddi) varlığı doğrulanır. Tüm veri sentetiktir; ekranda, günlükte, audit'te veya dışa aktarımda kişisel veri ya da gizli değer görünmez.

## Hazırlık

1. Boş bir veri klasörü ile başlayın (uygulamanın `%LocalAppData%\MonoBridgeDesktop` klasörünü yedekleyip boşaltın ya da test makinesinde temiz bir kullanıcı kullanın). Gerçek mağaza kimlik bilgisi girmeyin.
2. Sentetik fikstür: iki mağaza bağlantısı (`etsy / S1`, `trendyol / T1`), yerel bir XML beslemesi (üç ürün; en az bir başlık 100+ karakter, Türkçe karakterli: "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — sonbahar koleksiyonu, ğüşıöç"), bir yerel kanal planı, bir sipariş (müşteri alanları maskeli), kuyrukta bir sync işi. Otomatik test aynı fikstürü `Seed()` ile üretir.
3. Ekran ölçeği: senaryo üç kez koşulur — Windows görüntü ölçeği %100, %150 ve %200. Her turda aynı adımlar, aynı beklenen sonuçlar. Otomatik test ölçeği pencerenin içeriğine uygular ve pencereyi aynı oranda büyütür; DIP alanı değişmediği için semantik durum her ölçekte aynı olmak zorundadır.
4. Klavye: bir tur fare kullanmadan (yalnız Tab / Shift+Tab, Enter, Esc, ok tuşları ve kısayollar: Ctrl+K arama, Ctrl+1 pano, Ctrl+2 ürünler, Ctrl+B menü, Alt+← / Alt+→ gezinti izi, F1 kısayol listesi, F5 ürün listesi yenile) koşulur.

## Adımlar ve kabul ölçütleri

| # | Ekran / eylem | Beklenen |
|---|---------------|----------|
| 0 | Uygulama açılışı | Tasarım tokenları çözülür (açılışta uyarı yok), gövde yazı tipi ve boyutu token değerleridir, kısayol kataloğunda çakışma yoktur. Odak halkası klavye ile gelen odakta çizilir, fare ile gelen odakta çizilmez. |
| 1 | Pano (dashboard) | KPI kartları yerel veriden dolar; "Durumu yenile" sürerken meşgul nedeniyle kapalıdır ve sonra açılır. Mağaza filtresi iki mağazayı ve "tüm mağazalar"ı sunar. Bu oturumda sunulmayan bir mağazayı gösteren derin bağlantı (ör. `etsy|GHOST`) pencereyi hareket ettirmez ve nedenini söyler. Bir karttan ürüne inildiğinde ekmek kırıntısı ürünün SKU'sunu gösterir. |
| 2 | Ürün listesi ve çalışma alanı | İnilen ürün seçili gelir, düzenleyici canlıdır; uzun Türkçe ad kesilmeden (sarılarak / üç noktayla ve ipucuyla) görünür; hiçbir öğe kırpılmaz (1440 ve en dar genişlik 1150). "Ürünü sil" önce sorar; onay penceresinde varsayılan (Enter) güvenli düğme "Vazgeç"tir, "Sil" varsayılan değildir; Vazgeç ürünü korur. |
| 3 | XML okuma ve içe aktarma önizlemesi | Kaynak seçilir, "XML'i oku" ve "Önizle" gerçek boru hattında çalışır; adım şeridi canlıdır; önizleme tablosu beslemenin satırlarını listeler; ilerleme paneli aşamaları ve iptal sonucunu gerçek durumdan söyler (İptal istendi / Güvenli noktada duruyor / İptal edildi / Tamamlanmıştı). "Havuza aktar" bu senaryoda tıklanmaz (yerel yazımdır, canlı değildir). |
| 4 | Kanal yayın matrisi | Ürünler satır, mağazalar sütun olarak listelenir; yerel plan hücresi ve lejant görünür; "Toplu plan…" seçim olmadan reddeder; yanlış mağaza hücreleri gizlidir. |
| 5 | Sipariş çalışma alanı | Sipariş listede ve ayrıntıda görünür; müşteri ad / e-posta / telefon / adres maskelidir ve gerekçe girilmeden açılmaz; uzun ürün satırı kesilmez. |
| 6 | Rapor | "Sipariş listesi (CSV)" kartı kurulumu açar; "Çalıştır" sorguyu gerçek depoda koşturur ve sonucu ekranda gösterir; dosya yazılmaz (dışa aktarma ayrı bir onaylı adımdır). İptal sonucu satırı gerçek durumdan konuşur. |
| 7 | Ayarlar | Ağaç açılır; her giriş adlandırılmıştır ve Tab ile erişilir; devre dışı her komut nedenini söyler; ekranda kişisel veri veya gizli değer yoktur ("Yerel veri" satırı kullanıcı adını göstermez). |
| K | Klavye turu | Arama kutusundan başlayan Tab döngüsü kabuğun tamamını dolaşıp başa döner (kapan yok); Ctrl+1 panoyu, Ctrl+K aramayı açar; bağlı olmayan tuş yutulmaz. |
| S | Canlı yazım yok | Sync merkezinde fikstürün işi "Pending" (veya iptal edildiyse "Cancelled") kalır; hiçbir iş "Running / Succeeded / Failed" olmaz; hiçbir pazaryerine istek gitmez. |

Her ekranda ortak ölçütler (otomatik testte `Score()`):

- Kırpılan öğe yok (1440 DIP ve en dar genişlik 1150 DIP); sarma, üç nokta ve kaydırma kırpma sayılmaz.
- Ekrandaki metinlerin hiçbiri merkezi redaksiyondan geçince değişmez (e-posta, telefon, bearer, `anahtar=değer` gizli değer, kullanıcı profili yolu yok).
- Semantik durum (boş durumlar, hata bantları, meşgul kaplaması, tablo satır sayıları, sunulan eylemler) %100, %150 ve %200'de aynıdır.
- Her giriş ve komut bir ad duyurur; etkin her birim Tab ile erişilir ve döngü kapanır; odaklanabilir her girişin odak halkası vardır; devre dışı her giriş nedenini (ipucu veya #871 nedeni) taşır.

## Sonuç kaydı

Her tur için: tarih, ölçek (%100 / %150 / %200), klavye-yalnız (evet/hayır), her adımın geçti/kaldı durumu ve kalan bulguların ekran + öğe adı (metin değeri yazılmaz). Otomatik testin bulgu listesi aynı biçimdedir (`ekran: Bulgu: Sahip`).
