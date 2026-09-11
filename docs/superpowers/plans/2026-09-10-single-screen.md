# XML tek çalışma ekranı

Amaç: Kullanıcının kaynak ayarları, alan eşleştirme, fiyat/stok ve önizleme arasında sayfa değiştirmeden çalışması. .NET 8 WPF ve mevcut kayıt biçimi korunur.

- [x] MainWindow.xaml.cs: dört kaynak alt sekmesini kaldır; solda kaynak ve genel ayarlar, ortada alan eşleştirme, sağda fiyat/stok, altta ürün önizlemesi göster. Bölme çubuklarıyla boyutlandır.
- [x] XML yollarını ortak ObservableCollection üzerinden her eşleştirme satırında açılır listede sun. Otomatik önerileri koru, mevcut eşleştirmeyi ezme. Gelişmiş birleşik görsel yolları düzenlenebilir kalsın. Kaynak değişince eski seçenekleri temizle.
- [x] work/ui-smoke/Program.cs: gerçek WPF ağacında kaynak alt sekmesi olmadığını, XML alan seçicisinin seçeneklerle dolduğunu ve seçimin önizlemeye uygulandığını doğrula. Önce mevcut sürümde başarısızlığı gör.
- [x] 141 regresyon testi ve WPF akış kontrolünü çalıştır; ayrı Windows-TekEkran klasörüne Release yayınla. Kullanım notunu güncelle.

Uygulama bu oturumda executing-plans ile yürütülür. Canlı Etsy işlemi veya veri göçü yapılmaz.

