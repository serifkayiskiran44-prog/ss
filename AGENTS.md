# Çalışma kuralları
Önce PROJECT_SPEC.md ve TODO.md oku. .NET 8 WPF EXE ana uygulamadır; yeni giriş şifresi/login ekleme. Mevcut veriyi/credentialları koru. CommerceHub salt referans olarak korunur.
Her değişiklikte ilgili anlamlı testleri ve Release publish çalıştır. Testler geçici veri dizini kullanır; gerçek mağazada test yazması yapma. Kullanıcının yedi DEFERRED_BY_USER alanına yeni kod/ekran/model/test ekleme.

Test: dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release
Yayın: dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current
TODO ve IMPLEMENTATION_STATUS güncel tutulur; yerel kanal planlarını gerçek API olarak raporlama.

## Canlı write güvenlik kapısı
Canlı marketplace yazımı yalnız immutable preview + açık kullanıcı onayı + stale/revision kontrolü + idempotency/receipt + wrong-store/account guard ile yapılır. Gerçek marketplace'e otomatik test write yapılmaz. Secret/PAT/token/password/API key repo, fixture, log veya issue içine yazılmaz.

## Codex GitHub çalışma kuralı
Codex yerel `gh` oturumu, PAT/GITHUB_TOKEN veya etkileşimli GitHub girişi istemeyecek. Kullanıcıdan `gh auth login`, token veya GitHub oturumu talep etme. Kod/test/publish tamamlanınca branch'i commit edip `git push` yapman yeterlidir. `codex/issue-<numara>-<kisa-ad>` branch push'ları `.github/workflows/codex-pr-router.yml` tarafından otomatik PR'a çevrilir. PR/issue yorumu yazamadığın için işi durdurma veya kullanıcıya soru sorma.
