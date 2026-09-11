using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace TrMarketplaceHubDesktop;
public enum ImageMarketplace { Etsy, Ebay }
public sealed record PreparedMarketplaceImage(byte[] Bytes, int Width, int Height, string Status);
public static class MarketplaceImages
{
    public const int OutputLimit = 7 * 1024 * 1024;
    public static Task<PreparedMarketplaceImage> PrepareAsync(byte[] original, ImageMarketplace marketplace)
    {
        var completion = new TaskCompletionSource<PreparedMarketplaceImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { completion.SetResult(Prepare(original, marketplace)); }
            catch (Exception error) when (error is not OutOfMemoryException) {
                completion.SetException(new InvalidOperationException(error is InvalidOperationException ? error.Message : "Görsel çözülemedi. Geçerli JPEG, PNG veya GIF kullanın; bu Windows kurulumunda biçim desteklenmiyor olabilir."));
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    static PreparedMarketplaceImage Prepare(byte[] original, ImageMarketplace marketplace)
    {
        if (original.Length == 0 || original.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Görsel boş veya uygulamanın 20 MB giriş sınırını aşıyor.");
        using var input = new MemoryStream(original, false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 20000 || frame.PixelHeight > 20000 || (long)frame.PixelWidth * frame.PixelHeight > 40000000)
            throw new InvalidOperationException("Görsel uygulamanın 40 megapiksel / 20000 piksel giriş sınırını aşıyor.");
        BitmapSource source = frame;
        ushort orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                try { if (metadata.ContainsQuery(query)) { orientation = Convert.ToUInt16(metadata.GetQuery(query)); break; } } catch (NotSupportedException) { }
        var transform = new TransformGroup();
        switch (orientation)
        {
            case 2: transform.Children.Add(new ScaleTransform(-1,1)); break;
            case 3: transform.Children.Add(new RotateTransform(180)); break;
            case 4: transform.Children.Add(new ScaleTransform(1,-1)); break;
            case 5: transform.Children.Add(new ScaleTransform(-1,1)); transform.Children.Add(new RotateTransform(270)); break;
            case 6: transform.Children.Add(new RotateTransform(90)); break;
            case 7: transform.Children.Add(new ScaleTransform(-1,1)); transform.Children.Add(new RotateTransform(90)); break;
            case 8: transform.Children.Add(new RotateTransform(270)); break;
        }
        if (transform.Children.Count > 0) source = new TransformedBitmap(source, transform);
        var scale = Math.Min(1d, 4096d / Math.Max(source.PixelWidth, source.PixelHeight));
        if (marketplace == ImageMarketplace.Ebay) scale = Math.Max(scale, 500d / Math.Min(source.PixelWidth, source.PixelHeight));
        var width = (int)Math.Round(source.PixelWidth * scale); var height = (int)Math.Round(source.PixelHeight * scale);
        if (width > 4096 || height > 4096) throw new InvalidOperationException("Görsel çok dar/uzun. Kırpmadan 500 piksel kısa kenar ve uygulamanın 4096 piksel uzun kenar sınırı birlikte sağlanamıyor.");
        var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen()) { context.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height)); context.DrawImage(source,new Rect(0,0,width,height)); }
        var rendered = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); rendered.Render(visual);
        var rgb = new FormatConvertedBitmap(rendered,PixelFormats.Bgr24,null,0);
        byte[] bytes = [];
        foreach (var quality in new[] {90,80,70,60,50}) {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality }; encoder.Frames.Add(BitmapFrame.Create(rgb));
            using var output = new MemoryStream(); encoder.Save(output); bytes = output.ToArray();
            if (bytes.Length <= OutputLimit) break;
        }
        if (bytes.Length > OutputLimit) throw new InvalidOperationException("Görsel uygulamanın 7 MB çıktı sınırına indirilemedi.");
        var status = $"{source.PixelWidth}×{source.PixelHeight} → {width}×{height}, JPEG, {bytes.Length / 1024} KB. Orijinal korundu.";
        if (scale > 1) status += " Boyut büyütüldü; yeni ayrıntı üretmez.";
        if (marketplace == ImageMarketplace.Etsy && Math.Min(width,height) < 2000) status += " Etsy 2000×2000 veya üzerini önerir; mevcut görsel düşük çözünürlüklü, bu bir zorunlu minimum değildir.";
        if (marketplace == ImageMarketplace.Etsy && bytes.Length > 1024 * 1024) status += " Etsy 1 MB üzerindeki görsellerde yükleme yavaşlığı olabileceğini belirtir; bu zorunlu dosya sınırı değildir.";
        if (decoder.Frames.Count > 1) status += " Yalnız ilk kare kullanıldı.";
        return new(bytes,width,height,status);
    }
}
