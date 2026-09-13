# Sipariş istisna ve iptal/iade karar merkezi

```mermaid
flowchart LR
  O[OrdersStore] --> R[Reconcile]
  C[Catalog SKU eşlemeleri] --> R
  R --> Q[(OrderExceptions)]
  Q --> P{İptal / iade?}
  P -->|Hayır| M[Eksik veya ambiguous SKU kararı]
  P -->|Evet| V[OrderStockReceipt önizlemesi]
  V --> S{Kullanıcı onayı + güncel ürün sürümü}
  S -->|Hayır| W[Stok değişmez]
  S -->|Evet| A[(OrderStockRestores)]
```

İstisna kaydı `(marketplace, shop, order, type, eventKey)` ile tekilleştirilir. Stok geri koyma ayrıca sipariş başına tek restore kaydı tutar; aynı olay veya yeniden açılan karar ikinci kez stok ekleyemez. Eski ürün sürümü ile yeni sürüm arasında fark varsa işlem reddedilir ve yeni önizleme istenir.
