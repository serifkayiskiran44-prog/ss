using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;
public partial class MainWindow
{
 FrameworkElement BuildSync(){var sync=new SyncStore(dataDirectory);var panel=new StackPanel{Margin=new Thickness(20),MaxWidth=1100};panel.Children.Add(Heading("Sync merkezi"));panel.Children.Add(Hint("Yerel sync kuyruğu idempotency anahtarıyla çalışır. Bu ekran canlı marketplace yazımı yapmaz; başarısız işleri ve tekrar deneme durumunu yönetir."));var channel=new TextBox{Text="etsy",Width=120};var operation=new ComboBox{ItemsSource=new[]{"stock","price","product","order"},SelectedIndex=0,Width=120};var entity=new TextBox{Width=180};var version=new TextBox{Text="v1",Width=120};var grid=new DataGrid{AutoGenerateColumns=true,IsReadOnly=true,Height=350};var status=Hint("");void Refresh(){grid.ItemsSource=sync.List();}var enqueue=Button("Kuyruğa ekle",()=>{var job=sync.Enqueue(new SyncRequest(channel.Text.Trim(),operation.SelectedItem?.ToString()??"stock",entity.Text.Trim(),version.Text.Trim()));status.Text=$"Sync işi hazır: {job.Id}";Refresh();});var retry=Button("Seçili işi tekrar dene",()=>{if(grid.SelectedItem is not SyncJob job)throw new InvalidOperationException("Önce bir sync işi seçin.");sync.Retry(job.Id);status.Text="Sync işi tekrar kuyruğa alındı.";Refresh();});var row=new WrapPanel();row.Children.Add(new TextBlock{Text="Kanal",Margin=new Thickness(4),VerticalAlignment=VerticalAlignment.Center});row.Children.Add(channel);row.Children.Add(operation);row.Children.Add(entity);row.Children.Add(version);row.Children.Add(enqueue);row.Children.Add(retry);panel.Children.Add(row);panel.Children.Add(grid);panel.Children.Add(status);Refresh();return Scroll(panel);}
}
