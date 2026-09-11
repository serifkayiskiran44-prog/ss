# Etsy sipariş senkronu (M49)

`OrdersEtsyClient` resmi `getShopReceipts` read endpoint'ini `transactions_r` ile kullanır. Sayfalama 100 kayıtlık offset ile yapılır; isteğe bağlı `min_last_modified` ile son başarılı senkron zamanından itibaren daraltılabilir. Yanıt tamamlanmadan yerel kayıtlar değiştirilmez.

`OrdersStore.SaveBatch` mağaza + receipt ID birincil anahtarı ve güncelleme zamanı ile idempotent upsert yapar; eski yerel kargo olayları korunur. Stok kararı ayrıca `EtsyOrderStockDecisionService` üzerinden explicit approval ve transaction/receipt idempotency ile uygulanır. Eksik SKU veya mapping istisna merkezine gider; iptal/iade otomatik stok eklemez.
