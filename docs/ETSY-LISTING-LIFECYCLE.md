# Etsy listing yaşam döngüsü (M48)

- Yerel ürün `EtsyDrafts.Validate` ile zorunlu alanlardan geçmeden HTTP çağrısı yapmaz.
- Taslak oluşturma `listings_w` kapsamı ve açık kullanıcı onayı ile yapılır; belirsiz yanıt otomatik tekrar edilmez.
- Mevcut listing stok/fiyat güncellemesi `EtsyListingSyncService` preview + optimistic version + approval + SyncJob idempotency kapısından geçer.
- Yayınlama resmi `updateListing` PATCH çağrısında `state=active` ile yapılır; `listings_w` gerekir ve `PublishAsync` aynı stale/idempotency kapılarını kullanır.
- Görsel yükleme ayrı çağrıdır; credential image host'a taşınmaz. Eksik görsel veya doğrulanmamış yanıt yayın akışını otomatik tamamlamaz.
- Gerçek mağaza write'ı fake HTTP contract testleri dışında çalıştırılmadı.
