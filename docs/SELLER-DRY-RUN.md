# Sentetik satıcı provası (M50)

Sentetik fixture senaryosu XML ürününü yerel kataloğa alır, Etsy listing/order kararını fake adapter ile üretir, transaction stok düşümü uygular ve uygulama restart sonrasında aynı receipt'in ikinci stok hareketi üretmediğini doğrular. Gerçek marketplace HTTP write, credential, müşteri veya PII kullanılmaz.

Beklenen izlenebilirlik: XML import özeti, ürün kaydı, receipt/order stock receipt, stok hareketi, duplicate sonucu ve hata/audit katmanları. Gerçek mağaza credential'ı bulunmayan adımlar readiness/LIVE_API_BLOCKED olarak kalır.
