# Kanal yayın durumu ve ürün listeleme matrisi

```mermaid
flowchart LR
  P[CatalogProducts] --> M[Ürün × kanal × mağaza satırları]
  L[ChannelPlans] --> M
  S[SyncJobs] --> M
  A[MarketplaceConnections] --> M
  M --> F[Mapping / sync / auth / stale filtreleri]
  F --> U[Ürün veya kanal ekranına geçiş]
  U --> R[Yerel preview + connector onayı]
```

Matris, `ChannelPlans` anahtarındaki kanal + mağaza + ürün bileşimini korur; bir mağazanın listing ID'si başka mağazada görünmez. Capability yalnız ilgili connector kayıtlarından okunur. Bu ekran canlı marketplace değişikliği başlatmaz.
