# Üretim öncesi doğrulama

M20 ile izinli yerel akışlar aynı LocalAppData çalışma alanını kullanır: XML/Excel önizlemesi → ürün havuzu → kanal/mağaza planı → yerel sync kuyruğu → sipariş → onaylı merkezi stok kararı → hata merkezi. Her dış yazma işlemi mevcut önizleme, sürüm ve açık onay kontrollerinden geçer; desteklenmeyen veya sözleşmesi doğrulanmamış kanallar `LIVE_API_BLOCKED` / `NOT_CONFIGURED` olarak görünür.

Release doğrulama komutları:

```powershell
dotnet test ..\MonoBridgeDesktop.Tests\MonoBridgeDesktop.Tests.csproj -c Release --no-restore
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-M20-Installer
.\installer\Smoke-Test.ps1 -PackageDirectory .\Windows-M20-Installer
```

Kontrol edilen sınıflar:

- XML/Excel içe aktarma önizlemesi, alan kilitleri, duplicate SKU/barkod ve stale edit reddi.
- Kanal + mağaza ayrışması; aynı ürünün farklı kanallarda yerel planları birbirine karışmıyor.
- Sipariş stok düşümü duplicate replay, değişmiş replay, yetersiz stok, transaction rollback ve eşzamanlı oversell.
- Sync idempotency, stale preview, retry sınıfı/sınırı ve unsupported adapter için HTTP çağrısı yapılmaması.
- API timeout/401/403/429/5xx sınırları, cancellation ve OAuth token yenileme testleri.
- XML/sync/automation/marketplace hata saklamasında credential desenlerinin maskelenmesi.
- Büyük katalog listelerinde SQL filtre/sayfalama, DataGrid sanallaştırması ve arka plan snapshot yüklemesi.
- Kurulum/kaldırma smoke testi; kaldırma `%LOCALAPPDATA%\MonoBridgeDesktop` verisini silmiyor.
- Backup manifesti, SHA-256, staging doğrulaması, mevcut veri güvenlik yedeği ve atomic restore.

Fulfillment, varyant, bundle/set, hızlı düzenleme, kritik fiyat, XML varyant mapping ve hakediş/mutabakat `DEFERRED_BY_USER` kapsamındadır. Gerçek kargo veya doğrulanmamış marketplace write endpointleri bu sertleştirme turunda açılmaz.
