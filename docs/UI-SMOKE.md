# Core UI smoke kabul notu

M44 için mevcut WPF çekirdeği gözden geçirildi:

- Ürün, sipariş, XML, Excel, medya, kanal matrisi, sync, ayar ve tanılama tablolarında satır sanallaştırma/sayfalama kullanılır.
- Ürün havuzu ve kanal ürünlerinde 200 satır sayfalama; ilan ve sipariş akışında açık iptal/yenileme durumu vardır.
- Uzun XML, migration, dashboard, medya, toplu işlem ve sipariş okumaları UI thread dışında çalışır; ortak `RunAsync`/`SemaphoreSlim` kapısı aynı anda ikinci işlemi reddeder.
- Riskli yerel yazmalar önizleme + açık onay ister. Async butonlar işlem boyunca devre dışıdır; duplicate click yeni iş başlatmaz.
- Sol menü araması, breadcrumb, geri ve Ctrl+K global arama rotaları korunur.
- Boş veri, loading, hata ve başarı mesajları ürün/sipariş/XML panellerinde gösterilir; secret/error metinleri maskelenir.
- WPF klavye ve yüksek DPI davranışı kod seviyesinde standart kontroller/ölçülerle korunur; bu ortamda fiziksel ekran/CAPTCHA smoke çalıştırılmadı.

Deferred alanlar ve doğrulanmamış marketplace write işlemleri bu turda açılmadı.
