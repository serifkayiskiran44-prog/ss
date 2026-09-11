# Çalışma kuralları
Önce PROJECT_SPEC.md ve TODO.md oku. .NET 8 WPF EXE ana uygulamadır; yeni giriş şifresi/login ekleme. Mevcut veriyi/credentialları koru. CommerceHub salt referans olarak korunur.
Her değişiklikte ilgili anlamlı testleri ve Release publish çalıştır. Testler geçici veri dizini kullanır; gerçek mağazada test yazması yapma. Kullanıcının yedi DEFERRED_BY_USER alanına yeni kod/ekran/model/test ekleme.
Test: dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --no-restore
Yayın: dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current --no-restore
TODO ve IMPLEMENTATION_STATUS güncel tutulur; yerel kanal planlarını gerçek API olarak raporlama.

## Codex GitHub çalışma kuralı
Codex yerel `gh` oturumu, PAT/GITHUB_TOKEN veya etkileşimli GitHub girişi istemeyecek. Kullanıcıdan `gh auth login`, token veya GitHub oturumu talep etme. Kod/test/publish tamamlanınca branch'i commit edip `git push` yapman yeterlidir. `codex/issue-<numara>-<kisa-ad>` branch push'ları `.github/workflows/codex-pr-router.yml` tarafından otomatik PR'a çevrilir, issue/PR yorumları ve Claude review tetikleyicisi GitHub Actions tarafından yazılır. PR/issue yorumu yazamadığın için işi durdurma veya kullanıcıya soru sorma.
