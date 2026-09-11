# Çalışma kuralları
Önce PROJECT_SPEC.md ve TODO.md oku. .NET 8 WPF EXE ana uygulamadır; yeni giriş şifresi/login ekleme. Mevcut veriyi/credentialları koru. CommerceHub salt referans olarak korunur.
Her değişiklikte ilgili anlamlı testleri ve Release publish çalıştır. Testler geçici veri dizini kullanır; gerçek mağazada test yazması yapma. Kullanıcının yedi DEFERRED_BY_USER alanına yeni kod/ekran/model/test ekleme.
Test: dotnet test ../MonoBridgeDesktop.Tests/MonoBridgeDesktop.Tests.csproj -c Release
Yayın: dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current
TODO ve IMPLEMENTATION_STATUS güncel tutulur; yerel kanal planlarını gerçek API olarak raporlama.
