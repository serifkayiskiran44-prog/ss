using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class NesPanel
{
 static readonly HttpClient Http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};

 public static FrameworkElement Create(string? directory=null)
 {
  var root=new DockPanel{Margin=new Thickness(14)};var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
  header.Children.Add(Text("NES e-Fatura / e-Arşiv",22));header.Children.Add(Text("Bu ekran yalnız NES bağlantı ayarları ve fatura geçmişi içindir. Fatura hazırlama ve gönderme işlemleri Siparişler ekranındaki Fatura işlemleri düğmesinden yapılır."));var status=Text("NES ayarlarını kaydedin veya bağlantıyı doğrulayın.");status.FontWeight=FontWeights.SemiBold;header.Children.Add(status);
  var settingsStore=new NesSettingsStore(directory is null?null:Path.Combine(directory,"nes.bin"));var documents=new NesDocumentStore(directory);var api=new NesConnection(Http);
  var expander=new Expander{Header="NES bağlantı ve firma ayarları",IsExpanded=true,Margin=new Thickness(0,4,0,8)};header.Children.Add(expander);var panel=new StackPanel();expander.Content=panel;
  var form=new Grid();form.ColumnDefinitions.Add(new(){Width=new GridLength(170)});form.ColumnDefinitions.Add(new(){Width=new GridLength(360)});form.ColumnDefinitions.Add(new(){Width=new GridLength(170)});form.ColumnDefinitions.Add(new());panel.Children.Add(form);
  var apiKey=new PasswordBox();var einvoiceUrl=Box();var earchiveUrl=Box();var senderAlias=Box();var sourceApp=Box();var einvoiceSeries=Box();var earchiveSeries=Box();var companyTitle=Box();var taxNumber=Box();var taxOffice=Box();var address=Box();var city=Box();var district=Box();var email=Box();var phone=Box();
  Add(form,"API anahtarı",apiKey,0,0);Add(form,"e-Fatura servis kökü",einvoiceUrl,1,0);Add(form,"e-Arşiv servis kökü",earchiveUrl,2,0);Add(form,"Gönderici etiketi",senderAlias,3,0);Add(form,"Kaynak uygulama",sourceApp,4,0);Add(form,"e-Fatura serisi",einvoiceSeries,5,0);Add(form,"e-Arşiv serisi",earchiveSeries,6,0);
  Add(form,"Firma adı",companyTitle,0,2);Add(form,"VKN / TCKN",taxNumber,1,2);Add(form,"Vergi dairesi",taxOffice,2,2);Add(form,"Adres",address,3,2);Add(form,"İl",city,4,2);Add(form,"İlçe",district,5,2);Add(form,"E-posta / telefon",Pair(email,phone),6,2);
  var actions=new WrapPanel{Margin=new Thickness(0,6,0,0)};panel.Children.Add(actions);var save=Button(actions,"Ayarları güvenli kaydet");var test=Button(actions,"Bağlantıyı salt okunur doğrula");var refresh=Button(actions,"NES giden faturaları yenile");var delete=Button(actions,"Yerel NES ayarını sil");
  NesSettings Capture()=>new(){ApiKey=apiKey.Password.Trim(),EInvoiceBaseUrl=einvoiceUrl.Text.Trim(),EArchiveBaseUrl=earchiveUrl.Text.Trim(),SenderAlias=senderAlias.Text.Trim(),SourceApp=sourceApp.Text.Trim(),CompanyTitle=companyTitle.Text.Trim(),TaxNumber=taxNumber.Text.Trim(),TaxOffice=taxOffice.Text.Trim(),Address=address.Text.Trim(),City=city.Text.Trim(),District=district.Text.Trim(),Email=email.Text.Trim(),Phone=phone.Text.Trim(),EInvoiceSeries=einvoiceSeries.Text.Trim(),EArchiveSeries=earchiveSeries.Text.Trim()};
  void Display(NesSettings s){apiKey.Password=s.ApiKey;einvoiceUrl.Text=s.EInvoiceBaseUrl;earchiveUrl.Text=s.EArchiveBaseUrl;senderAlias.Text=s.SenderAlias;sourceApp.Text=s.SourceApp;companyTitle.Text=s.CompanyTitle;taxNumber.Text=s.TaxNumber;taxOffice.Text=s.TaxOffice;address.Text=s.Address;city.Text=s.City;district.Text=s.District;email.Text=s.Email;phone.Text=s.Phone;einvoiceSeries.Text=s.EInvoiceSeries;earchiveSeries.Text=s.EArchiveSeries;}
  Display(new NesSettings());try{var saved=settingsStore.Load();if(saved is not null){Display(saved);status.Text="Şifreli NES ayarları yüklendi.";}}catch(Exception ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}
  var tabs=new TabControl();root.Children.Add(tabs);var remoteTab=new TabItem{Header="NES giden faturalar"};var remoteGrid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,EnableRowVirtualization=true,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=30,FrozenColumnCount=3};
  foreach(var (label,path,width) in new[]{("Fatura No","DocumentNumber",155d),("Sipariş No","MarketplaceOrderNumber",125d),("Mağaza","MarketplaceSupplierId",105d),("Pazaryeri","MarketplaceProvider",100d),("Belge Türü","DocumentKindLabel",90d),("Tarih","IssueDate",135d),("Tutar","PayableAmount",95d),("Döviz","Currency",65d),("Durum","Status",130d),("Kaynak Kayıt","SourceAppRecordId",220d)})remoteGrid.Columns.Add(new DataGridTextColumn{Header=label,Binding=new System.Windows.Data.Binding(path),Width=width,MinWidth=width});
  remoteTab.Content=remoteGrid;tabs.Items.Add(remoteTab);var localTab=new TabItem{Header="Gönderim kayıtları"};var localGrid=new DataGrid{AutoGenerateColumns=true,IsReadOnly=true,EnableRowVirtualization=true};localTab.Content=localGrid;tabs.Items.Add(localTab);
  var busy=false;async Task Run(Func<Task> action){if(busy)return;busy=true;panel.IsEnabled=tabs.IsEnabled=false;try{await action();}catch(ArgumentException ex){status.Text=ex.Message;}catch(InvalidOperationException ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}catch(OperationCanceledException){status.Text="NES işlemi zaman aşımına uğradı.";}catch{status.Text="NES işlemi tamamlanamadı; mevcut kayıtlar korundu.";}finally{busy=false;panel.IsEnabled=tabs.IsEnabled=true;}}
  async Task ReloadRemote(){var settings=Capture();var invoices=await api.ReadOutgoingAsync(settings,NesDocumentKind.EInvoice);var archives=await api.ReadOutgoingAsync(settings,NesDocumentKind.EArchive);var all=invoices.Concat(archives).OrderByDescending(x=>x.IssueDate).ToList();remoteGrid.ItemsSource=all;var reconciliation=NesOrderReconciler.Reconcile(new OrdersStore(directory),all);localGrid.ItemsSource=documents.List();status.Text=$"NES bağlantısı doğrulandı: {invoices.Count} e-Fatura, {archives.Count} e-Arşiv · {reconciliation.Label}.";}
  save.Click+=async(_,_)=>await Run(()=>{settingsStore.Save(Capture());status.Text="NES ayarları Windows kullanıcı profilinde şifreli kaydedildi.";return Task.CompletedTask;});test.Click+=async(_,_)=>await Run(ReloadRemote);refresh.Click+=async(_,_)=>await Run(async()=>{await ReloadRemote();tabs.SelectedItem=remoteTab;});delete.Click+=async(_,_)=>await Run(()=>{settingsStore.Delete();apiKey.Clear();status.Text="Yerel şifreli NES ayarları silindi.";return Task.CompletedTask;});
  root.Loaded+=(_,_)=>localGrid.ItemsSource=documents.List();return root;
 }

 static TextBlock Text(string text,int size=13)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkSlateGray,Margin=new Thickness(3,3,3,7)};
 static TextBox Box()=>new(){MinWidth=220,Margin=new Thickness(2)};
 static Button Button(Panel panel,string label){var button=new Button{Content=label,Padding=new Thickness(10,5,10,5),Margin=new Thickness(3)};panel.Children.Add(button);return button;}
 static UIElement Pair(UIElement first,UIElement second){var panel=new Grid();panel.ColumnDefinitions.Add(new());panel.ColumnDefinitions.Add(new());panel.Children.Add(first);Grid.SetColumn(second,1);panel.Children.Add(second);return panel;}
 static void Add(Grid grid,string label,UIElement input,int row,int column){while(grid.RowDefinitions.Count<=row)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});var text=Text(label,12);Grid.SetRow(text,row);Grid.SetColumn(text,column);grid.Children.Add(text);Grid.SetRow(input,row);Grid.SetColumn(input,column+1);grid.Children.Add(input);}
}
