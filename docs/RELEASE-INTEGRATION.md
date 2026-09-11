# M45 release integration doğrulaması

`codex/issue-80-release-integration`, `codex/issue-64-ui-smoke` head'inden oluşturuldu. Branch içeriği M1–M44 commit zincirini korur; eski branch'ler silinmedi ve force-push yapılmadı.

## Kontrol matrisi

- SQLite store'lar migration-safe açılış ve tekrar açılış testlerinden geçer.
- Audit/support export, API health, migration ve onboarding akışlarında credential/token/password değerleri redacted kalır.
- Etsy/eBay dispatch ve bulk/local write akışlarında explicit approval, optimistic stale sürüm ve duplicate/idempotency kapıları vardır.
- Sipariş stok düşümü transaction/rollback ve supplier XML stok kilidi ile korunur.
- `DEFERRED_BY_USER` alanları ve doğrulanmamış marketplace write işlemleri açılmadı.

## Release doğrulama

```text
dotnet test ..\MonoBridgeDesktop.Tests\MonoBridgeDesktop.Tests.csproj -c Release
344/344 passed
dotnet build TrMarketplaceHubDesktop.csproj -c Release
0 warnings / 0 errors
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-M45-Release-Integration
```

Publish çıktısı credential içermez; kullanıcı verisi LocalAppData'da kalır.
