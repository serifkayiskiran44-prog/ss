# Çalışma kuralları
Önce PROJECT_SPEC.md ve TODO.md oku. .NET 8 WPF EXE ana uygulamadır; yeni giriş şifresi/login ekleme. Mevcut veriyi/credentialları koru. CommerceHub salt referans olarak korunur.
Her değişiklikte ilgili anlamlı testleri ve Release publish çalıştır. Testler geçici veri dizini kullanır; gerçek mağazada test yazması yapma.

## Kapsam güncellemesi — 2026-09-11
Kullanıcı önceki yedi `DEFERRED_BY_USER` alanını artık geliştirme kapsamına aldı. Varyant sistemi, bundle/paket/set, hızlı satır içi düzenleme, kritik fiyat, XML varyant mapping, Fulfillment Core ve Hakediş/Mutabakat Core bundan sonra **IN_SCOPE** kabul edilir. Eski dokümanlarda geçen `DEFERRED_BY_USER` satırları bu güncel kararla geçersizdir. Bu alanları kör kopya olarak değil; Entegra iş akışı referansı + resmi marketplace/servis sözleşmesi + özgün .NET 8/WPF mimarisi ile geliştir.

Uygulama login/password/user-account sistemi hâlâ eklenmeyecek; kullanıcı ayrıca istemedikçe bu ürün kararı korunur. Secret/PAT/token/password/API key repo, issue, log veya fixture içine yazılmaz. Gerçek marketplace'e otomatik test write yapılmaz. Canlı write somut preview + açık kullanıcı onayı + stale/idempotency + wrong-shop koruması gerektirir. Doğrulanmamış endpoint/header/scope/API davranışı uydurulmaz.

Test: dotnet test ../MonoBridgeDesktop.Tests/MonoBridgeDesktop.Tests.csproj -c Release
Yayın: dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current
TODO ve IMPLEMENTATION_STATUS güncel tutulur; yerel kanal planlarını gerçek API olarak raporlama.

## Codex GitHub çalışma kuralı
Codex yerel `gh` oturumu, PAT/GITHUB_TOKEN veya etkileşimli GitHub girişi istemeyecek. Kullanıcıdan `gh auth login`, token veya GitHub oturumu talep etme. Kod/test/publish tamamlanınca branch'i commit edip `git push` yapman yeterlidir. `codex/issue-<numara>-<kisa-ad>` branch push'ları `.github/workflows/codex-pr-router.yml` tarafından otomatik PR'a çevrilir. PR/issue yorumu yazamadığın için işi durdurma veya kullanıcıya soru sorma.
