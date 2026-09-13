# Excel şablon ve aktarım merkezi

```mermaid
flowchart LR
  P[Excel profili] --> H[Kolon/alias + kültür + varsayılan]
  H --> X[XLSX önizleme]
  X --> D{CREATE / UPDATE / SKIP / ERROR}
  D -->|ERROR| E[Hata Excel'i]
  D -->|CREATE/UPDATE/SKIP| C[Satır seçimi]
  C --> A[Atomik katalog import + undo journal]
  O[Filtreli ürün havuzu] --> EX[Profil alanlarıyla dışa aktar]
```

Profil dosyaya bağlı değildir; yerel `excel-profiles.db` içinde tutulur. SKU birincil, barkod ikincil eşleştirme anahtarıdır. Profil önizlemesi tamamlanmadan toplu yazma başlamaz. Secret veya credential alanları profil şemasına alınmaz; varyant/bundle alanları kullanıcı kararıyla kapsam dışıdır.
