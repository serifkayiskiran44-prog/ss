using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Etsy;

namespace TrMarketplaceHubDesktop;
public sealed partial class EtsyWorkspacePanel
{
    UIElement BuildAccountShopProducts(UIElement specialized)
    {
        if(scopedConnection is null)return specialized;
        var tabs=new TabControl{Name="EtsyAccountProductSections"};
        var common=new MarketplaceShopProductsPanel(scopedConnection.Id,directory);common.BulkPreviewRequested+=HandleAccountBulkPreview;
        tabs.Items.Add(new TabItem{Header="Hesap ürünleri",Content=common});
        tabs.Items.Add(new TabItem{Header="Etsy işlemleri",Content=specialized});
        return tabs;
    }
    void HandleAccountBulkPreview(MarketplaceShopBulkOperation operation,MarketplaceShopSelectionSnapshot selection)
    {
        var connection=CurrentScopedConnection();
        if(selection.ConnectionId!=connection.Id||selection.ConnectionRevision!=connection.Revision)
            throw new InvalidOperationException("Mağaza hesabı değişti; seçimi yenileyin.");
        if(operation is MarketplaceShopBulkOperation.Taxonomy or MarketplaceShopBulkOperation.Properties or MarketplaceShopBulkOperation.Shipping or MarketplaceShopBulkOperation.Readiness){sections.SelectedIndex=1;settings.SelectedIndex=1;return;}
        var target=operation switch{
            MarketplaceShopBulkOperation.CreatePreview=>EtsyOperation.CreateDraft,
            MarketplaceShopBulkOperation.PricePreview=>EtsyOperation.Price,
            MarketplaceShopBulkOperation.StockPreview=>EtsyOperation.Stock,
            MarketplaceShopBulkOperation.ContentPreview=>EtsyOperation.Content,
            _=>throw new InvalidOperationException("Bu Etsy işlemi uzman önizlemeye bağlanmadı.")};
        _=Run(()=>Preview(target,selection.ProductIds));
    }
    UIElement BuildControl()
    {
        var grid=new Grid{Margin=new Thickness(6)};
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var top=new StackPanel();
        search.Width=300;search.ToolTip="Ürün adı, stok kodu, barkod";
        var filters=new Border{Visibility=Visibility.Collapsed,Child=Bar(Field("Ürün durumu",stateFilter))};
        var bulk=new Border{Visibility=Visibility.Collapsed};
        var toggleFilters=ActionButton("Detaylı filtreleme",()=>filters.Visibility=filters.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);toggleFilters.Name="EtsyToggleFilters";
        var toggleBulk=ActionButton("Toplu işlemler",()=>bulk.Visibility=bulk.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);toggleBulk.Name="EtsyToggleBulk";
        top.Children.Add(Bar(Field("Arama (ürün adı, stok kodu, barkod)",search),ActionButton("Ara",()=>{page=0;RefreshProducts();}),toggleFilters,toggleBulk,AsyncButton("Etsy kontrol / yenile",PullListings),ActionButton("Ürün kartını aç",OpenProduct),creationHandoffButton,remoteDeactivateHandoffButton));
        top.Children.Add(filters);
        var actions=new WrapPanel();
        foreach(var op in new[]{EtsyOperation.CreateDraft,EtsyOperation.PriceAndStock,EtsyOperation.Price,EtsyOperation.Stock,EtsyOperation.Content,EtsyOperation.Publish,EtsyOperation.Deactivate})
        {var captured=op;actions.Children.Add(AsyncButton(OperationLabel(op)+" · önizle",()=>Preview(captured)));}
        actions.Children.Add(ActionButton("SKU ile eşleştir",MatchProducts));
        actions.Children.Add(templateChoice); actions.Children.Add(ActionButton("Seçililere şablonu ata",AssignTemplate));
        bulk.Child=Scroll(actions);bulk.MaxHeight=115;top.Children.Add(bulk);
        send.Click+=(_,_)=>{if(plan is not null)ShowPreview(plan);};top.Children.Add(Bar(send,Note("Taslak oluşturma ve yayına alma ayrı işlemlerdir. Gönderimden önce seçili ürünlerin önizlemesini inceleyin.")));
        var topScroll=Scroll(top);topScroll.MaxHeight=200;grid.Children.Add(topScroll);
        products.FrozenColumnCount=2;Column(products,"Ürün kodu","Sku",110);Column(products,"Ürün adı","Title",300);Column(products,"Durum","State",105);Column(products,"Yerel adet","Stock",80);Column(products,"Etsy adet","EtsyStock",80);Column(products,"Etsy satış fiyatı","EtsyPrice",115);Column(products,"Etsy döviz","EtsyCurrency",80);Column(products,"Etsy ilan ID","ListingId",115);Column(products,"Barkod","Barcode",140);Column(products,"Yerel fiyat","Price",90);Column(products,"Yerel döviz","Currency",85);Column(products,"Kategori","Category",210);Column(products,"Marka","Brand",130);Column(products,"Son işlem / hata","Detail",300);
        Grid.SetRow(products,1);grid.Children.Add(products);products.MouseDoubleClick+=(_,_)=>Local(OpenProduct);
        products.SelectionChanged+=(_,_)=>{count.Text=$"Sayfa {page+1} · {products.Items.Count} ürün · {products.SelectedItems.Count} seçili";};
        var footer=Bar(count,ActionButton("Sayfayı seç",()=>products.SelectAll()),ActionButton("Seçimi kaldır",()=>products.UnselectAll()),ActionButton("‹ Önceki",()=>{page--;RefreshProducts();}),ActionButton("Sonraki ›",()=>{page++;RefreshProducts();}));Grid.SetRow(footer,2);grid.Children.Add(footer);
        stateFilter.SelectionChanged+=(_,_)=>{page=0;RefreshProducts();};search.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){page=0;RefreshProducts();}};
        return grid;
    }
    static string OperationLabel(EtsyOperation op)=>op switch {EtsyOperation.CreateDraft=>"Taslak oluştur",EtsyOperation.Price=>"Yalnız fiyat",EtsyOperation.Stock=>"Yalnız stok",EtsyOperation.PriceAndStock=>"Fiyat ve stok",EtsyOperation.Content=>"Ürün bilgileri",EtsyOperation.Publish=>"Yayına al",EtsyOperation.Deactivate=>"Satışa kapat",_=>op.ToString()};
    UIElement BuildHistory()
    {
        Column(history,"Tarih","CreatedUtc",160);Column(history,"Ürün kodu","Sku",120);Column(history,"Etsy ilan ID","ListingId",120);Column(history,"Durum","Status",160);Column(history,"Sonuç / hata","Detail",540);
        var dock=new DockPanel{Margin=new Thickness(8)};var button=ActionButton("İşlem geçmişini yenile",LoadState);DockPanel.SetDock(button,Dock.Top);dock.Children.Add(button);dock.Children.Add(history);return dock;
    }
    void AssignTemplate()
    {
        var ids=RequireSelection();var template=Selected(templateChoice);if(template.Length==0)throw new InvalidOperationException("Önce şablon seçin.");
        foreach(var id in ids){var profile=state.Profiles.FirstOrDefault(p=>p.ProductId==id);if(profile is null){profile=new(){ProductId=id};state.Profiles.Add(profile);}profile.TemplateId=template;}
        SaveState();summary.Text=$"{ids.Count} ürün için şablon yerelde kaydedildi. Gönderilecek değerleri önizleyin.";
    }
    void MatchProducts()
    {
        var ids=RequireSelection().ToHashSet();var service=new EtsyWorkspaceService(directory,http);var rows=service.Match(state,catalog.Products().Where(p=>ids.Contains(p.Id)).ToList());
        var table=Table("EtsyMatchPreview");Column(table,"SKU","Sku",120);Column(table,"Ürün","Title",270);Column(table,"İlan ID","ListingId",110);Column(table,"Durum","Status",100);Column(table,"Açıklama","Detail",320);table.ItemsSource=rows;
        var window=Dialog("SKU eşleştirme önizlemesi",1000,550);var dock=new DockPanel();var apply=ActionButton($"{rows.Count(r=>r.CanMatch)} kesin eşleşmeyi kaydet",()=>{service.ApplyMatches(state,rows.Where(r=>r.CanMatch).ToArray());LoadState();ClearPreview();window.Close();});apply.IsEnabled=rows.Any(r=>r.CanMatch);DockPanel.SetDock(apply,Dock.Bottom);dock.Children.Add(apply);dock.Children.Add(table);window.Content=dock;window.ShowDialog();
    }
    async Task Preview(EtsyOperation operation,IReadOnlyList<string>? exactProductIds=null)
    {
        var ids=exactProductIds is null?PreviewProductIds(operation):Array.AsReadOnly(exactProductIds.ToArray());
        ClearPreview();var c=await Authorized();
        summary.Text="Seçili ürünler ve Etsy mağazası karşılaştırılıyor…";
        plan=await new EtsyWorkspaceService(directory,http).PreviewAsync(c,ids,operation,lifetime.Token);
        if(operation==EtsyOperation.CreateDraft&&creationHandoffRequestId.Length>0)CompleteCreationHandoff(plan);
        send.IsEnabled=true;summary.Text=$"{plan.Rows.Count} satır · {plan.Rows.Count(r=>r.CanSend)} gönderilebilir · {plan.Rows.Count(r=>!r.CanSend)} kontrol gerekli";ShowPreview(plan);
    }
    void ShowPreview(EtsyOperationPlan preview)
    {
        var window=Dialog(OperationLabel(preview.Operation)+" — gönderim önizlemesi",1100,650);var dock=new DockPanel();
        var explanation=Note($"Mağaza: {state.ShopName} / {preview.ShopId}\n{preview.Rows.Count} ürün · {preview.Rows.Count(r=>r.CanSend)} geçerli satır. Hatalı satırlar gönderilmez."+(preview.Operation==EtsyOperation.Publish?"\nYayına alma Etsy listeleme ücreti doğurabilir.":""));DockPanel.SetDock(explanation,Dock.Top);dock.Children.Add(explanation);
        var json=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=140};var detail=new Expander{Header="Seçili satırın gönderilecek alanları",Content=json};DockPanel.SetDock(detail,Dock.Bottom);dock.Children.Add(detail);
        var approve=new Button{Content="Bu önizlemeyi onayla ve Etsy’ye gönder",IsEnabled=preview.Rows.Any(r=>r.CanSend),Margin=new Thickness(8),MinHeight=36};DockPanel.SetDock(approve,Dock.Bottom);dock.Children.Add(approve);
        var table=Table("EtsySendPreview");Column(table,"SKU","Sku",110);Column(table,"Ürün","Title",270);Column(table,"İlan ID","ListingId",110);Column(table,"İşlem","Action",130);Column(table,"Değişiklik / hata","Detail",430);table.ItemsSource=preview.Rows;table.SelectionChanged+=(_,_)=>json.Text=(table.SelectedItem as EtsyPreviewRow)?.PayloadJson??"";dock.Children.Add(table);window.Content=dock;
        approve.Click+=async(_,_)=>
        {
            if(MessageBox.Show(window,$"{state.ShopName} mağazasında {preview.Rows.Count(r=>r.CanSend)} ürüne ‘{OperationLabel(preview.Operation)}’ işlemi uygulanacak. Onaylıyor musunuz?","Etsy gönderim onayı",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            approve.IsEnabled=false;
            try{var c=await Authorized();var receipts=await new EtsyWorkspaceService(directory,http).SendAsync(c,preview.Id,true,lifetime.Token);table.ItemsSource=null;LoadState();ClearPreview();summary.Text=string.Join(" · ",receipts.GroupBy(r=>r.Status).Select(g=>$"{g.Count()} {g.Key}"));window.Close();sections.SelectedIndex=2;}
            catch(Exception ex){ClearPreview();try{LoadState();}catch{summary.Text="İşlem geçmişi okunamadı; uygulamayı yenileyin.";}MessageBox.Show(window,Safe(ex),"Etsy sonucu",MessageBoxButton.OK,MessageBoxImage.Warning);window.Close();}
        };
        window.ShowDialog();
    }
    Window Dialog(string title,double width,double height)=>new(){Title=title,Width=width,Height=height,MinWidth=700,MinHeight=450,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner};
}
