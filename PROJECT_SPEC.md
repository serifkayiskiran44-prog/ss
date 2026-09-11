# MonoBridge Desktop — güncel proje kararı

2026-09-11: Kullanıcı .NET 8 WPF masaüstü EXE üzerinde devam edilmesini, önceki kapsamda kalan çalışmaların buraya uyarlanmasını istedi. Programa giriş parolası, login veya kullanıcı hesabı ekranı eklenmeyecek. API hesaplarının mevcut şifreli saklanması korunur; bu uygulama giriş şifresi değildir.

Mevcut kod temel alınır. CommerceHub silinmez; davranış/test/doküman referansıdır. Web kodu veya PostgreSQL verisi otomatik taşındı sayılmaz. Önceki kapsam kaynağı ../CommerceHub/PROJECT_SPEC.md ve docs/18-CORE-160-REQUEST.md; teknoloji/auth maddelerini bu karar geçersiz kılar.

## Kapsam güncellemesi — tam Entegra sınıfı operasyon programı
Kullanıcı önceki yedi `DEFERRED_BY_USER` alanını artık geliştirme kapsamına aldı. **Varyant sistemi, bundle/paket/set, hızlı satır içi düzenleme, kritik fiyat, XML varyant mapping, fulfillment core ve hakediş/mutabakat core artık IN_SCOPE.** Eski dokümanlardaki bu yedi alanın ertelendiğine dair ifadeler bu güncel kararla geçersizdir.

Hedef yalnız birkaç API bağlantısı değil; dropshipping ağırlıklı, Entegra seviyesinde kapsamlı çok-kanallı entegrasyon ve günlük operasyon merkezi oluşturmaktır. Entegra'nın kaynak kodu/markası/ikonları/metinleri/piksel düzeni kopyalanmaz. Yetkili statik analiz, eğitim/transkriptler ve resmi özellik kanıtları davranış ve bilgi mimarisi referansıdır; uygulama kodu özgün .NET 8/WPF olacaktır.

Öncelik zinciri: ortak ürün/katalog → tedarikçi/XML/Excel → varyant/seçenek → stok/fiyat/kur/maliyet/kârlılık → kategori/marka/özellik → kanal ürünleri → Etsy üretim akışı → Trendyol pilot → diğer marketplace connectorları → sipariş/iade/fatura/kargo/fulfillment → hakediş/mutabakat/raporlama → dayanıklılık/güvenlik/release.

Dropshipping gereksinimleri özellikle önemlidir: çoklu tedarikçi, source-of-truth/field lock, feed anomaly koruması, source-missing grace period, güvenlik stoğu, kanal bazlı fiyat formülleri, kur dönüşümü, supplier health, ürün/listing drift, stok/fiyat delta ve güvenli toplu senkron.

Canlı API yazımı somut önizleme onayı gerektirir. Secret/PAT/token/password/API key repo/log/issue içine yazılmaz. Doğrulanmamış endpoint/scope/header/API davranışı uydurulmaz. Gerçek marketplace'e otomatik test write yapılmaz; fake transport/contract fixture/sandbox kullanılır.

Veriler LocalAppData/MonoBridgeDesktop içinde kalır. Yeni yayın dizini Windows-Current; .NET çalışma zamanını yanında içerir. EXE bu klasörün diğer dosyalarıyla birlikte tutulmalıdır.

En son yayınlar tarihsel kanıttır; worker her yeni görev sonunda gerçek test ve publish sonucunu TODO/IMPLEMENTATION_STATUS içinde güncel tutar.
