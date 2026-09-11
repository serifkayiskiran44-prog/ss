using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TrMarketplaceHubDesktop;

public static class MarketplaceImagePanel
{
    public static FrameworkElement Create()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Dosyadan görsel hazırla: boyut, yön ve JPEG dönüşümü otomatik. Orijinal değişmez. Etsy gönderiminde ilk fotoğraf da otomatik hazırlanır. eBay için bu bölüm yalnız dosya hazırlar; ilan göndermez.", TextWrapping = TextWrapping.Wrap });
        var target = new ComboBox { ItemsSource = new[] { "eBay", "Etsy" }, SelectedIndex = 0, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,8,0,8) };
        var action = new Button { Content = "Fotoğraf seç ve otomatik hazırla", HorizontalAlignment = HorizontalAlignment.Left };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) };
        action.Click += async (_,_) => {
            var picker = new OpenFileDialog { Filter = "Görseller|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff;*.webp", Multiselect = false };
            if (picker.ShowDialog() != true) return;
            var marketplace = target.SelectedIndex == 0 ? ImageMarketplace.Ebay : ImageMarketplace.Etsy;
            action.IsEnabled = false; status.Text = "Görsel hazırlanıyor…";
            try {
                if (new FileInfo(picker.FileName).Length > EtsyDrafts.ImageLimit) throw new InvalidOperationException("Görsel 20 MB giriş sınırını aşıyor.");
                var image = await MarketplaceImages.PrepareAsync(await File.ReadAllBytesAsync(picker.FileName), marketplace);
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "PreparedImages");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, marketplace + "-" + Guid.NewGuid().ToString("N") + ".jpg");
                await File.WriteAllBytesAsync(path, image.Bytes);
                status.Text = image.Status + "\nHazır dosya: " + path + "\nDosya adresi (ürün görsel alanına eklenebilir): " + new Uri(path).AbsoluteUri;
            } catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { status.Text = error.Message; }
            finally { action.IsEnabled = true; }
        };
        panel.Children.Add(target); panel.Children.Add(action); panel.Children.Add(status);
        return new GroupBox { Header = "Otomatik görsel hazırlama", Content = panel, Padding = new Thickness(12), Margin = new Thickness(0,8,0,8) };
    }
}
