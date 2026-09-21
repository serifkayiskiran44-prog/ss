using System.Printing;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public enum OrderPrintKind { Barcode, CargoLabel, Invoice }
public sealed record OrderPrintDocument(string Title,string PlainText,string BarcodeValue);
public sealed record OrderPrintHistory(DateTimeOffset PrintedAt,string OrderId,string Kind,string Printer,int Copies);

public sealed class OrderPrintHistoryStore
{
    readonly string connectionString;
    public OrderPrintHistoryStore(string? directory=null){directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"print-history.db")}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS print_history(printed_utc TEXT NOT NULL,order_id TEXT NOT NULL,kind TEXT NOT NULL,printer TEXT NOT NULL,copies INTEGER NOT NULL)";cmd.ExecuteNonQuery();}
    public void Add(IEnumerable<OrderSnapshot> orders,OrderPrintKind kind,string printer,int copies){using var c=Open();using var tx=c.BeginTransaction();foreach(var order in orders){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO print_history VALUES($u,$o,$k,$p,$c)";cmd.Parameters.AddWithValue("$u",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$o",order.OrderId);cmd.Parameters.AddWithValue("$k",kind.ToString());cmd.Parameters.AddWithValue("$p",printer);cmd.Parameters.AddWithValue("$c",copies);cmd.ExecuteNonQuery();}tx.Commit();}
    public IReadOnlyList<OrderPrintHistory> List(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT printed_utc,order_id,kind,printer,copies FROM print_history ORDER BY printed_utc DESC LIMIT 500";using var r=cmd.ExecuteReader();var rows=new List<OrderPrintHistory>();while(r.Read())rows.Add(new(DateTimeOffset.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4)));return rows;}
    SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
}

public static class Code128B
{
    static readonly string[] Patterns={"212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212","112232","122132","122231","113222","123122","123221","223211","221132","221231","213212","223112","312131","311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321","112313","132113","132311","211313","231113","231311","112133","112331","132131","113123","113321","133121","313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111","314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114","122411","142112","142211","241211","221114","413111","241112","134111","111242","121142","121241","114212","124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113","114311","411113","411311","113141","114131","311141","411131","211412","211214","211232","2331112"};
    public static IReadOnlyList<int> Encode(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Any(c=>c<32||c>126))throw new ArgumentException("Code 128 barkodu yalnız ASCII 32-126 karakterlerini destekler.");
        var codes=new List<int>{104};codes.AddRange(value.Select(c=>(int)c-32));var checksum=104;for(var i=1;i<codes.Count;i++)checksum+=codes[i]*i;codes.Add(checksum%103);codes.Add(106);
        return codes.SelectMany(code=>Patterns[code].Select(c=>c-'0')).ToArray();
    }
}

public static class OrderPrintComposer
{
    public static OrderPrintDocument Compose(OrderSnapshot order,OrderPrintKind kind)
    {
        OrdersRules.Validate(order);var shipment=order.Shipments.FirstOrDefault();var barcode=kind==OrderPrintKind.Barcode?order.Items.FirstOrDefault()?.Barcode??order.OrderId:shipment?.TrackingNumber??order.OrderId;
        var text=kind switch
        {
            OrderPrintKind.Barcode=>$"BARKOD\n{order.Items.FirstOrDefault()?.Title}\nSKU: {order.Items.FirstOrDefault()?.Sku}\n{barcode}",
            OrderPrintKind.CargoLabel=>$"KARGO ETİKETİ\n{order.Marketplace} / {order.OrderId}\n{order.ShippingName}\n{order.ShippingAddress}\n{order.ShippingDistrict} / {order.ShippingCity}\n{shipment?.Carrier} · {shipment?.TrackingNumber}",
            _=>$"FATURA / SİPARİŞ ÖZETİ\nSipariş: {order.OrderId}\nAlıcı: {order.BillingName}\n{order.BillingAddress}\n{string.Join("\n",order.Items.Select(x=>$"{x.Quantity} x {x.InvoiceTitle.DefaultIfEmpty(x.Title)} · {x.UnitPrice:0.00}"))}\nToplam: {order.Total:0.00} {order.Currency}\nFatura No: {order.InvoiceNumber}",
        };
        return new(kind switch{OrderPrintKind.Barcode=>"Barkod",OrderPrintKind.CargoLabel=>"Kargo etiketi",_=>"Fatura"},text,barcode);
    }
    static string DefaultIfEmpty(this string value,string fallback)=>string.IsNullOrWhiteSpace(value)?fallback:value;
}

public static class OrderPrintCenter
{
    public static void Show(Window? owner,IReadOnlyList<OrderSnapshot> orders,string? directory=null)
    {
        if(orders.Count==0)throw new InvalidOperationException("Yazdırmak için en az bir sipariş seçin.");
        var history=new OrderPrintHistoryStore(directory);
        var win=new Window{Title="Yazdırma merkezi",Width=900,Height=680,Owner=owner,WindowStartupLocation=owner is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new Thickness(14)};win.Content=root;var top=new WrapPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var kind=new ComboBox{Width=170,ItemsSource=Enum.GetValues<OrderPrintKind>().Select(x=>new KeyValuePair<OrderPrintKind,string>(x,x switch{OrderPrintKind.Barcode=>"Ürün barkodu",OrderPrintKind.CargoLabel=>"Kargo etiketi",_=>"Fatura / sipariş özeti"})),DisplayMemberPath="Value",SelectedValuePath="Key",SelectedIndex=0,Margin=new Thickness(3)};top.Children.Add(kind);
        var copies=new TextBox{Width=55,Text="1",Margin=new Thickness(3),ToolTip="Kopya sayısı"};top.Children.Add(copies);var preview=new FlowDocumentScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(preview);
        FlowDocument Build(){var document=new FlowDocument{PagePadding=new Thickness(25),FontFamily=new FontFamily("Segoe UI")};foreach(var order in orders){var data=OrderPrintComposer.Compose(order,(OrderPrintKind)(kind.SelectedValue??OrderPrintKind.Barcode));document.Blocks.Add(new Paragraph(new Run(data.Title)){FontSize=20,FontWeight=FontWeights.Bold});document.Blocks.Add(new Paragraph(new Run(data.PlainText)){FontSize=13});if(data.BarcodeValue.Length>0){var canvas=new Canvas{Width=560,Height=90};var x=5d;var black=true;foreach(var width in Code128B.Encode(data.BarcodeValue)){if(black)canvas.Children.Add(new System.Windows.Shapes.Rectangle{Fill=Brushes.Black,Width=width*2,Height=70,Margin=new Thickness(x,0,0,0)});x+=width*2;black=!black;}document.Blocks.Add(new BlockUIContainer(canvas));}document.Blocks.Add(new Paragraph(new Run(" ")){BreakPageBefore=order!=orders[0]});}return document;}
        void Refresh()=>preview.Document=Build();kind.SelectionChanged+=(_,_)=>Refresh();var refresh=new Button{Content="Önizlemeyi yenile",Margin=new Thickness(3)};refresh.Click+=(_,_)=>Refresh();top.Children.Add(refresh);
        var print=new Button{Content="Yazdır",Margin=new Thickness(3)};print.Click+=(_,_)=>{try{if(!int.TryParse(copies.Text,out var count)||count is<1 or>20)throw new InvalidOperationException("Kopya sayısı 1-20 arasında olmalı.");var dialog=new PrintDialog();if(dialog.ShowDialog()!=true)return;for(var i=0;i<count;i++)dialog.PrintDocument(((IDocumentPaginatorSource)Build()).DocumentPaginator,"MonoBridge sipariş çıktısı");history.Add(orders,(OrderPrintKind)(kind.SelectedValue??OrderPrintKind.Barcode),dialog.PrintQueue?.FullName??"Windows yazıcısı",count);MessageBox.Show("Yazdırma tamamlandı ve geçmişe kaydedildi.","Yazdırma merkezi");}catch(Exception ex){MessageBox.Show(MarketplaceConnectionStore.Redact(ex.Message),"Yazdırma merkezi",MessageBoxButton.OK,MessageBoxImage.Warning);}};top.Children.Add(print);
        var showHistory=new Button{Content="Yazdırma geçmişi",Margin=new Thickness(3)};showHistory.Click+=(_,_)=>{var historyWindow=new Window{Title="Yazdırma geçmişi",Width=800,Height=500,Owner=win,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new DataGrid{ItemsSource=history.List(),IsReadOnly=true,AutoGenerateColumns=true}};historyWindow.ShowDialog();};top.Children.Add(showHistory);Refresh();win.ShowDialog();
    }
}
