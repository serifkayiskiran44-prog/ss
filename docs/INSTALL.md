# MonoBridge Desktop kurulum ve veri güvenliği

`Windows-M20-Installer` klasörü self-contained `win-x64` yayın çıktısıdır. İnternet veya ayrı .NET kurulumu gerektirmez. `installer/Install-MonoBridge.ps1` bu klasörü `%LOCALAPPDATA%/Programs/MonoBridgeDesktop` altına kopyalar ve Başlat menüsü kısayolu oluşturur. Kurulum veya yeni sürüm güncellemesi mevcut `%LOCALAPPDATA%/MonoBridgeDesktop` veri klasörünü silmez ve üzerine yazmaz.

Yeni paket üretmek için `installer/Build-Package.ps1` çalıştırılabilir; script gerçek `dotnet publish` çıktısını zip'ler. `installer/Uninstall-MonoBridge.ps1` yalnız uygulama dosyalarını ve kısayolu kaldırır, kullanıcı verisini korur.

Uygulamadaki Ayarlar → Sürüm, yedek ve taşıma bölümünden kullanıcı onayıyla zip yedeği alınabilir. Yedek manifestosu dosya boyutu ve SHA-256 ile doğrulanır. Geri yükleme önce mevcut klasörü güvenlik zip'ine alır, geçici klasörde tüm dosyaları doğrular ve dizin değişimiyle atomik olarak uygular; başarısızlıkta eski klasör geri alınır. `.bin` credential dosyaları açılmaz veya çözülmez; mevcut Windows kullanıcı hesabına bağlı DPAPI byte'ları olduğu gibi taşınır.

Güncelleme kontrolü için doğrulanmamış bir uzak endpoint çağrılmaz. Uygulama sürümü `1.0.0` proje sürümünden ve yerel `installed-version.txt` bilgisinden izlenir.
