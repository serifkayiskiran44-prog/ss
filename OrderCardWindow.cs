using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;

namespace TrMarketplaceHubDesktop;

public static class OrderCardRules
{
    public static OrderSnapshot NewManual()=>new()
    {
        Marketplace="Manuel",ShopId="default",OrderId="",Currency="TRY",RawStatus="Yeni Sipariş",
        PaymentStatus="Bekliyor",Source="Yerel / manuel",OrderDate=DateTimeOffset.Now,AddedAt=DateTimeOffset.Now,
        UpdatedAt=DateTimeOffset.UtcNow,SourceUpdatedAt=DateTimeOffset.UtcNow
    };

    public static void ValidateManual(OrderSnapshot order)
    {
        if(order.Items.Count==0)throw new ArgumentException("Sipariş kartına en az bir ürün ekleyin.");
        if(order.Items.Any(x=>string.IsNullOrWhiteSpace(x.Sku)||string.IsNullOrWhiteSpace(x.Title)||x.Quantity<=0||!x.UnitPrice.HasValue||x.UnitPrice<0))throw new ArgumentException("Her ürün için SKU, ürün adı, pozitif adet ve KDV dâhil fiyat zorunlu.");
        var totals=OrderTotalsCalculator.Calculate(order);order.NetTotal=totals.Net;order.VatTotal=totals.Vat;order.Total=totals.Gross;
        order.UpdatedAt=DateTimeOffset.UtcNow;order.SourceUpdatedAt=order.UpdatedAt;OrdersRules.Validate(order);
    }

    public static string FinancialSummary(OrderSnapshot order)
    {
        var totals=OrderTotalsCalculator.Calculate(order);var culture=CultureInfo.GetCultureInfo("tr-TR");
        string Money(decimal value)=>value.ToString("N2",culture)+" "+order.Currency;
        return $"Net: {Money(totals.Net)}    KDV: {Money(totals.Vat)}    İndirim: {Money(totals.Discount)}    Komisyon: {Money(totals.Commission)}    Genel toplam: {Money(totals.Gross)}"+(totals.HasMismatch?$"    Kaynak farkı: {Money(totals.SourceDifference)}":"");
    }
}

public static class OrderCardWindow
{
    static readonly Brush Ink=new SolidColorBrush(Color.FromRgb(23,49,59));
    static readonly Brush Muted=new SolidColorBrush(Color.FromRgb(87,112,125));
    static readonly Brush Teal=new SolidColorBrush(Color.FromRgb(22,115,123));
    static readonly Brush Surface=new SolidColorBrush(Color.FromRgb(245,247,248));

    public static void ShowExisting(OrderSnapshot order,string? directory,Action? changed=null,Action? catalogChanged=null)=>Show(order.Copy(),directory,false,changed,catalogChanged);
    public static void ShowNew(string? directory,Action? changed=null)=>Show(OrderCardRules.NewManual(),directory,true,changed,null);

    static void Show(OrderSnapshot order,string? directory,bool editable,Action? changed,Action? catalogChanged)=>Build(order,directory,editable,changed,catalogChanged).ShowDialog();

    /// Builds the card without showing it (tests and callers decide how to show it).
    public static Window Build(OrderSnapshot order,string? directory,bool editable,Action? changed,Action? catalogChanged)
    {
        var win=new Window{Title=editable?"Yeni sipariş kartı":$"Sipariş kartı · {order.OrderId}",Width=1380,Height=820,MinWidth=980,MinHeight=650,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=Application.Current is { } app&&app.Dispatcher.CheckAccess()&&app.MainWindow is { IsLoaded: true } mainWindow?mainWindow:null,Background=Surface};
        var root=new DockPanel{Margin=new Thickness(14)};win.Content=root;
        var header=new Grid{Margin=new Thickness(0,0,0,10)};header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var titles=new StackPanel();titles.Children.Add(new TextBlock{Text=editable?"Yeni sipariş":"Sipariş kartı",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=Ink});titles.Children.Add(new TextBlock{Text=editable?"Müşteri, ürün, KDV ve teslimat bilgilerini tamamlayın.":$"{order.Marketplace} · {order.ShopId} · {order.OrderId}",Foreground=Muted,Margin=new Thickness(0,4,0,0)});header.Children.Add(titles);
        var headerActions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(headerActions,1);header.Children.Add(headerActions);
        Button Action(string text){var b=new Button{Content=text,Margin=new Thickness(4),Padding=new Thickness(12,7,12,7),MinWidth=100};headerActions.Children.Add(b);return b;}
        if(!editable)
        {
            var invoice=Action("Fatura işlemleri");invoice.Click+=(_,_)=>OrderInvoiceDialog.Show(order,directory,null,changed);
            var stock=Action("Stok işlemi");stock.Name="OrderApplyStockButton";
            // Physical sales already left the physical shelf, and an order whose account/product match needs review must not move online stock.
            if(order.IsPhysicalSale||order.ReviewRequired){stock.IsEnabled=false;stock.ToolTip=order.IsPhysicalSale?"Fiziksel satış çevrimiçi stoktan düşülemez.":"Eşleşmesi incelenmesi gereken sipariş stoktan düşülemez.";}
            stock.Click+=(_,_)=>
            {
                try
                {
                    var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(directory);var receipt=catalog.GetOrderStockStatus(order.Marketplace,order.ShopId,order.OrderId);
                    if(receipt!=null){MessageBox.Show(win,$"Bu siparişin stoğu {receipt.AppliedUtc.ToLocalTime():g} tarihinde işlendi. Stok tekrar düşülmedi.","Sipariş stoğu",MessageBoxButton.OK,MessageBoxImage.Information);return;}
                    var preview=new TrMarketplaceHubDesktop.Catalog.OrderStockDecisionService(catalog).CreatePreview(order);var products=catalog.Products();var lines=string.Join(Environment.NewLine,order.Items.GroupBy(x=>x.Sku,StringComparer.Ordinal).Select(group=>{var product=products.SingleOrDefault(x=>x.Sku==group.Key);var quantity=group.Sum(x=>x.Quantity);return product is null?$"{group.Key}: katalog eşleşmesi yok":$"{group.Key}: {product.Stock} → {product.Stock-quantity} (−{quantity})";}));
                    if(MessageBox.Show(win,lines+"\n\nBu yerel stok hareketi uygulansın mı?","Sipariş stok önizlemesi",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
                    new TrMarketplaceHubDesktop.Catalog.OrderStockDecisionService(catalog).ApplyApproved(preview,true);catalogChanged?.Invoke();MessageBox.Show(win,"Sipariş stoğu işlendi. Aynı sipariş tekrar düşülmez.","Sipariş stoğu",MessageBoxButton.OK,MessageBoxImage.Information);
                }
                catch(Exception ex){MessageBox.Show(win,MarketplaceConnectionStore.Redact(ex.Message),"Sipariş stoğu",MessageBoxButton.OK,MessageBoxImage.Warning);}
            };
        }
        var close=Action("Kapat");close.Click+=(_,_)=>win.Close();
        var boxes=new Dictionary<string,TextBox>(StringComparer.Ordinal);

        var footer=new Grid{Margin=new Thickness(0,10,0,0)};footer.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var status=new TextBlock{Foreground=Muted,VerticalAlignment=VerticalAlignment.Center,Text=editable?"Sipariş henüz kaydedilmedi.":"Kaynak verileri salt okunur gösterilir."};footer.Children.Add(status);
        if(editable){var save=new Button{Content="Siparişi kaydet",Background=Teal,Foreground=Brushes.White,Padding=new Thickness(18,9,18,9),Margin=new Thickness(4)};Grid.SetColumn(save,1);footer.Children.Add(save);save.Click+=(_,_)=>{try{Capture();OrderCardRules.ValidateManual(order);new OrdersStore(directory).SaveManual(order);changed?.Invoke();win.Close();}catch(Exception ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);status.Foreground=Brushes.Firebrick;}};}

        var tabs=new TabControl{Background=Brushes.White};root.Children.Add(tabs);
        TextBox Field(Grid grid,int row,int col,string label,string value,bool multi=false)
        {
            while(grid.RowDefinitions.Count<=row)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var holder=new StackPanel{Margin=new Thickness(8)};holder.Children.Add(new TextBlock{Text=label,Foreground=Muted,Margin=new Thickness(0,0,0,4)});
            var box=new TextBox{Text=value,IsReadOnly=!editable,MinWidth=190,Padding=new Thickness(7,5,7,5),TextWrapping=multi?TextWrapping.Wrap:TextWrapping.NoWrap,AcceptsReturn=multi,MinHeight=multi?72:0};holder.Children.Add(box);Grid.SetRow(holder,row);Grid.SetColumn(holder,col);grid.Children.Add(holder);return box;
        }
        Grid FormGrid(int columns=3){var g=new Grid{Margin=new Thickness(10)};for(var i=0;i<columns;i++)g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});return g;}
        void AddTab(string title,UIElement content)=>tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});

        var general=FormGrid();boxes["Marketplace"]=Field(general,0,0,"Satış platformu",order.Marketplace);boxes["ShopId"]=Field(general,0,1,"Mağaza",order.ShopId);boxes["OrderId"]=Field(general,0,2,"Sipariş numarası",order.OrderId);boxes["PlatformReference"]=Field(general,1,0,"Platform referans no",order.PlatformReference);boxes["RawStatus"]=Field(general,1,1,"Sipariş durumu",order.RawStatus);boxes["PaymentStatus"]=Field(general,1,2,"Ödeme durumu",order.PaymentStatus);boxes["PaymentType"]=Field(general,2,0,"Ödeme tipi",order.PaymentType);boxes["Currency"]=Field(general,2,1,"Para birimi",order.Currency);Field(general,2,2,"Kaynak",order.Source).IsReadOnly=true;Field(general,3,0,"Sipariş tarihi",order.OrderDate?.ToLocalTime().ToString("g")??"—").IsReadOnly=true;Field(general,3,1,"Entegrasyona eklenme",order.AddedAt?.ToLocalTime().ToString("g")??"—").IsReadOnly=true;Field(general,3,2,"Son güncelleme",order.UpdatedAt.ToLocalTime().ToString("g")).IsReadOnly=true;AddTab("Genel",general);

        var customer=FormGrid();boxes["CustomerName"]=Field(customer,0,0,"Müşteri",order.CustomerName);boxes["CustomerId"]=Field(customer,0,1,"Müşteri ID",order.CustomerId);boxes["CustomerCode"]=Field(customer,0,2,"Müşteri kodu",order.CustomerCode);boxes["CustomerEmail"]=Field(customer,1,0,"Müşteri e-postası",order.CustomerEmail);boxes["CustomerPhone"]=Field(customer,1,1,"Telefon",order.CustomerPhone);boxes["TaxNumber"]=Field(customer,1,2,editable?"VKN / TCKN":"Faturada kullanılacak VKN / TCKN",editable?order.TaxNumber:OrderInvoiceRules.DisplayIdentity(order));boxes["CustomerTaxOffice"]=Field(customer,2,0,"Vergi dairesi",order.CustomerTaxOffice);boxes["BillingName"]=Field(customer,2,1,"Fatura adı / unvanı",order.BillingName);boxes["BillingEmail"]=Field(customer,2,2,"Fatura e-postası",order.BillingEmail);boxes["BillingAddress"]=Field(customer,3,0,"Fatura adresi",order.BillingAddress,true);boxes["BillingCountry"]=Field(customer,3,1,"Fatura ülkesi",order.BillingCountry);boxes["BillingCity"]=Field(customer,3,2,"Fatura ili",order.BillingCity);boxes["BillingDistrict"]=Field(customer,4,0,"Fatura ilçesi",order.BillingDistrict);Field(customer,4,1,"e-Fatura mükellefi",order.EInvoiceAvailable switch{true=>"Evet",false=>"Hayır",_=>"Bilinmiyor"}).IsReadOnly=true;boxes["InvoiceType"]=Field(customer,4,2,"Fatura tipi",order.InvoiceType);boxes["InvoiceNumber"]=Field(customer,5,0,"Fatura numarası",order.InvoiceNumber);boxes["InvoiceStatus"]=Field(customer,5,1,"Fatura durumu",order.InvoiceStatus);boxes["InvoiceUrl"]=Field(customer,6,0,"Fatura bağlantısı",order.InvoiceUrl);Grid.SetColumnSpan(boxes["InvoiceUrl"].Parent as FrameworkElement??boxes["InvoiceUrl"],3);AddTab("Müşteri ve fatura",customer);

        var shipping=FormGrid();boxes["ShippingName"]=Field(shipping,0,0,"Teslim alan",order.ShippingName);boxes["ShippingPhone"]=Field(shipping,0,1,"Sevk telefonu",order.ShippingPhone);boxes["ShippingCountry"]=Field(shipping,0,2,"Sevk ülkesi",order.ShippingCountry);boxes["ShippingAddress"]=Field(shipping,1,0,"Sevk adresi",order.ShippingAddress,true);boxes["ShippingCity"]=Field(shipping,1,1,"Sevk ili",order.ShippingCity);boxes["ShippingDistrict"]=Field(shipping,1,2,"Sevk ilçesi",order.ShippingDistrict);var shipmentGrid=DataGrid(false);shipmentGrid.ItemsSource=order.Shipments;shipmentGrid.Margin=new Thickness(8);shipmentGrid.Height=260;shipmentGrid.Columns.Add(Column("Paket","Id",130));shipmentGrid.Columns.Add(Column("Kargo firması","Carrier",160));shipmentGrid.Columns.Add(Column("Takip no","TrackingNumber",150));shipmentGrid.Columns.Add(Column("Durum","State",140));shipmentGrid.Columns.Add(Column("Ödeme","PaymentParty",120));shipmentGrid.Columns.Add(Column("Teslimat tipi","DeliveryType",140));Grid.SetRow(shipmentGrid,2);Grid.SetColumnSpan(shipmentGrid,3);shipping.Children.Add(shipmentGrid);AddTab("Kargo",shipping);

        var productsPanel=new DockPanel{Margin=new Thickness(10)};var productActions=new WrapPanel();DockPanel.SetDock(productActions,Dock.Top);productsPanel.Children.Add(productActions);var productGrid=DataGrid(editable);productGrid.ItemsSource=order.Items;productGrid.Columns.Add(Column("Ürün ID","ProductId",90));productGrid.Columns.Add(Column("SKU","Sku",130));productGrid.Columns.Add(Column("Barkod","Barcode",130));productGrid.Columns.Add(Column("Ürün adı","Title",280));productGrid.Columns.Add(Column("Fatura adı","InvoiceTitle",200));productGrid.Columns.Add(Column("Adet","Quantity",65));productGrid.Columns.Add(Column("Net fiyat","NetUnitPrice",90));productGrid.Columns.Add(Column("KDV %","VatRate",70));productGrid.Columns.Add(Column("KDV dâhil fiyat","UnitPrice",110));productGrid.Columns.Add(Column("İndirim","DiscountAmount",85));productGrid.Columns.Add(Column("Komisyon","CommissionAmount",85));productsPanel.Children.Add(productGrid);
        if(editable){var add=new Button{Content="+ Ürün ekle",Margin=new Thickness(3),Padding=new Thickness(10,5,10,5)};var remove=new Button{Content="Seçili ürünü çıkar",Margin=new Thickness(3),Padding=new Thickness(10,5,10,5)};productActions.Children.Add(add);productActions.Children.Add(remove);add.Click+=(_,_)=>{order.Items.Add(new OrderItem{VatRate=20,Quantity=1});productGrid.Items.Refresh();};remove.Click+=(_,_)=>{if(productGrid.SelectedItem is OrderItem item){order.Items.Remove(item);productGrid.Items.Refresh();}};}
        var totalsText=new TextBlock{Text=OrderCardRules.FinancialSummary(order),FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Ink,TextAlignment=TextAlignment.Right,Margin=new Thickness(8)};DockPanel.SetDock(totalsText,Dock.Bottom);productsPanel.Children.Add(totalsText);productGrid.CellEditEnding+=(_,_)=>win.Dispatcher.BeginInvoke(()=>totalsText.Text=OrderCardRules.FinancialSummary(order));AddTab("Ürünler",productsPanel);

        var finance=FormGrid();Field(finance,0,0,"Net tutar",(order.NetTotal??OrderTotalsCalculator.Calculate(order).Net).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,0,1,"KDV tutarı",(order.VatTotal??OrderTotalsCalculator.Calculate(order).Vat).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,0,2,"Genel toplam",(order.Total??OrderTotalsCalculator.Calculate(order).Gross).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,1,0,"İndirim tutarı",(order.DiscountTotal??0).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,1,1,"Komisyon tutarı",(order.CommissionTotal??0).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,1,2,"İndirimsiz toplam",(order.UndiscountedTotal??0).ToString("N2")+" "+order.Currency).IsReadOnly=true;Field(finance,2,0,"Fatura sağlayıcısı",order.InvoiceProvider).IsReadOnly=true;Field(finance,2,1,"Fatura numarası",order.InvoiceNumber).IsReadOnly=true;Field(finance,2,2,"Fatura durumu",order.InvoiceStatus).IsReadOnly=true;AddTab("Tutar ve fatura",finance);

        var history=new StackPanel{Margin=new Thickness(12)};foreach(var shipment in order.Shipments){history.Children.Add(new TextBlock{Text=$"{shipment.Id} · {shipment.Carrier} · {OrdersRules.Label(shipment.State)}",FontWeight=FontWeights.SemiBold,Foreground=Ink,Margin=new Thickness(0,8,0,3)});foreach(var entry in shipment.Events)history.Children.Add(new TextBlock{Text=$"{entry.At.ToLocalTime():g}  {OrdersRules.Label(entry.State)}  ·  {entry.Source}",Foreground=Muted,Margin=new Thickness(12,2,0,2)});}if(order.Shipments.Count==0)history.Children.Add(new TextBlock{Text="Henüz kargo veya durum geçmişi yok.",Foreground=Muted});AddTab("Durum geçmişi",history);
        var notes=FormGrid(1);boxes["SellerNote"]=Field(notes,0,0,"Satıcı / operasyon notu",order.SellerNote,true);AddTab("Notlar",notes);

        void Capture()
        {
            string V(string key)=>boxes.GetValueOrDefault(key)?.Text.Trim()??"";
            order.Marketplace=V("Marketplace");order.ShopId=V("ShopId");order.OrderId=V("OrderId");order.PlatformReference=V("PlatformReference");order.RawStatus=V("RawStatus");order.PaymentStatus=V("PaymentStatus");order.PaymentType=V("PaymentType");order.Currency=V("Currency").ToUpperInvariant();order.CustomerName=V("CustomerName");order.CustomerId=V("CustomerId");order.CustomerCode=V("CustomerCode");order.CustomerEmail=V("CustomerEmail");order.BillingEmail=V("BillingEmail");order.CustomerPhone=V("CustomerPhone");order.TaxNumber=V("TaxNumber");order.CustomerTaxOffice=V("CustomerTaxOffice");order.BillingName=V("BillingName");order.InvoiceType=V("InvoiceType");order.BillingAddress=V("BillingAddress");order.BillingCountry=V("BillingCountry");order.BillingCity=V("BillingCity");order.BillingDistrict=V("BillingDistrict");order.ShippingName=V("ShippingName");order.ShippingPhone=V("ShippingPhone");order.ShippingAddress=V("ShippingAddress");order.ShippingCountry=V("ShippingCountry");order.ShippingCity=V("ShippingCity");order.ShippingDistrict=V("ShippingDistrict");order.SellerNote=V("SellerNote");
        }
        return win;
    }

    static DataGrid DataGrid(bool editable)=>new(){AutoGenerateColumns=false,IsReadOnly=!editable,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=30,Background=Brushes.White};
    static DataGridTextColumn Column(string title,string path,double width)=>new(){Header=title,Binding=new Binding(path){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=width};
}
