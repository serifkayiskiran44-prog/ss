using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;
public sealed record TrendyolProductRow(CatalogProduct Product,string IntegrationCode,string RemoteStatus,decimal? RemotePrice,int? RemoteStock)
{
    public string Id=>Product.Id;public string Sku=>Product.Sku;public string Name=>Product.Name;public string Category=>Product.Category;public string Brand=>Product.Brand;public decimal Price=>Product.Price;public int Stock=>Product.Stock;
    public string StatusKey {get;init;}="unmatched";
    public string StatusDetail {get;init;}="";
    public string MappingSummary {get;init;}="";
    public bool MappingIncomplete {get;init;}
    public bool Approved {get;init;}
    public long? CategoryId {get;init;}
    public string Barcode=>Product.Barcode;
    public string Gtin=>Product.Gtin;
    public decimal? RemoteListPrice {get;init;}
    public string CreateMessage {get;init;}="";
    public string UpdateMessage {get;init;}="";
    public string Listed=>StatusKey=="onSale"?"● Yayında":StatusKey is "rejected" or "blacklisted" or "locked"?"× "+RemoteStatus:"• "+RemoteStatus;
    public System.Windows.Media.Brush RowBackground=>StatusKey is "rejected" or "blacklisted" or "locked" or "documentRequired"?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,222,176)):System.Windows.Media.Brushes.Transparent;
    public System.Windows.Media.Brush StatusColor=>StatusKey is "onSale"?System.Windows.Media.Brushes.SeaGreen:StatusKey is "rejected" or "blacklisted" or "locked"?System.Windows.Media.Brushes.Firebrick:StatusKey is "pendingApproval" or "documentRequired"?System.Windows.Media.Brushes.DarkGoldenrod:System.Windows.Media.Brushes.SlateGray;
}
public sealed partial class TrendyolWorkspacePanel
{
    readonly DataGrid products=Grid("TrendyolProducts",("Ürün kodu","Sku",90),("Ürün adı","Name",280),("Yayın durumu","Listed",120),("Adet","Stock",45),("T. stok","RemoteStock",55),("T. satış fiyatı","RemotePrice",90),("T. liste fiyatı","RemoteListPrice",90),("Marka","Brand",85),("GTIN","Gtin",110),("Trendyol barkodu","IntegrationCode",120),("Kategori ID","CategoryId",75),("Kategori","Category",180),("Ürün ekleme / hata mesajı","CreateMessage",240),("Ürün güncelleme mesajı","UpdateMessage",240),("Yerel barkod","Barcode",110),("Eşleştirme","MappingSummary",210));
    readonly DataGrid preview=Grid("TrendyolPreview",("SKU","Sku",110),("Ürün","Name",220),("Barkod","Barcode",150),("İşlem","Status",100),("Değişiklik / hata","Detail",600));
    readonly TextBox payload=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=180,Margin=new(5)};
    readonly TextBox productSearch=Box();
    readonly ComboBox mode=new(){ItemsSource=Enum.GetValues<TrendyolOperation>().Select(x=>new Mode(x,ModeLabel(x))).ToArray(),DisplayMemberPath="Label",SelectedIndex=3,MinWidth=160,Margin=new(3)};
    readonly TextBlock productCount=T("");
    readonly TextBlock selectionSummary=T("Ürün seçin.",true),controlSummary=T("",true),previewSummary=T("");
    readonly ComboBox statusFilter=new(){Name="TrendyolStatusFilter",ItemsSource=new[]{"Tümü","Yayında","Onay bekliyor","Reddedildi","Belge gerekli","Eşleşmedi","Eksik eşleştirme","Onaylı","Satışta değil","Arşivde","Kilitli","Satışa kapatılmış","Mağazada bulunamadı"},SelectedIndex=0,MinWidth=180,Margin=new(3)};
    readonly DockPanel productListView=new(){Name="TrendyolProductListView"},productEditorView=new(){Name="TrendyolProductEditorView",Visibility=Visibility.Collapsed},productPreviewView=new(){Name="TrendyolProductPreviewView",Visibility=Visibility.Collapsed};
    readonly TextBlock linkedBarcode=T("",true),productDesi=T("",true);
    readonly TextBox integration=Box(),origin=Box(),model=Box(),title=Box(),description=Box(),salePrice=Box(),listPrice=Box();
    readonly ComboBox category=Combo("Path"),brand=Combo(),delivery=Combo();
    readonly StackPanel attributes=new();
    readonly Dictionary<long,Func<TrendyolAttributeSelection?>> attributeInputs=new();
    readonly TextBlock editTitle=T("Ürün seçin");
    string? editingId;
    long editorRevision;
    int productOffset;
    bool populating;
    bool editorDirty;
    sealed record Mode(TrendyolOperation Value,string Label);
    static string ModeLabel(TrendyolOperation mode)=>mode switch{TrendyolOperation.Create=>"Yeni ürün ekle",TrendyolOperation.Price=>"Yalnız fiyat",TrendyolOperation.Stock=>"Yalnız stok",TrendyolOperation.PriceAndStock=>"Fiyat ve stok",TrendyolOperation.Delivery=>"Termin süresi",TrendyolOperation.ShippingDetails=>"Desi, kargo ve adresler",TrendyolOperation.UpdateUnapproved=>"Onaysız ürünü düzelt",_=>"Başlık ve açıklama"};
    FrameworkElement BuildProducts()
    {
        var host=new System.Windows.Controls.Grid();host.Children.Add(productListView);host.Children.Add(productEditorView);host.Children.Add(productPreviewView);host.Children.Add(productMatchView);BuildProductMatching();
        var top=BuildControlToolbar();DockPanel.SetDock(top,Dock.Top);productListView.Children.Add(top);
        var bottom=new DockPanel();var paging=new WrapPanel();paging.Children.Add(B("Sayfayı seç",()=>products.SelectAll()));paging.Children.Add(B("Seçimi kaldır",()=>products.UnselectAll()));paging.Children.Add(B("‹",()=>{productOffset=Math.Max(0,productOffset-PageSize);RefreshProducts();}));paging.Children.Add(B("›",()=>{productOffset+=PageSize;RefreshProducts();}));paging.Children.Add(productCount);paging.Children.Add(T("Sayfa limiti"));paging.Children.Add(pageSize);DockPanel.SetDock(paging,Dock.Right);bottom.Children.Add(paging);selectionSummary.MaxHeight=48;bottom.Children.Add(selectionSummary);DockPanel.SetDock(bottom,Dock.Bottom);productListView.Children.Add(bottom);productListView.Children.Add(products);
        ConfigureControlGrid();
        var back=B("← Ürün listesine dön",()=>{if(!DiscardEditorChanges())return;ShowProductView("list");});DockPanel.SetDock(back,Dock.Top);productEditorView.Children.Add(back);productEditorView.Children.Add(BuildProductEditor());
        var reviewTop=new StackPanel();var reviewActions=new WrapPanel();reviewActions.Children.Add(B("← Ürün listesine dön",()=>ShowProductView("list")));reviewActions.Children.Add(Primary(send));reviewTop.Children.Add(reviewActions);reviewTop.Children.Add(previewSummary);DockPanel.SetDock(reviewTop,Dock.Top);productPreviewView.Children.Add(reviewTop);
        var json=new Expander{Header="Teknik ayrıntılar · gönderilecek alanlar",Content=payload};DockPanel.SetDock(json,Dock.Bottom);productPreviewView.Children.Add(json);productPreviewView.Children.Add(preview);
        products.SelectionChanged+=(_,_)=>{InvalidatePreview();if(products.SelectedItem is TrendyolProductRow row){EditProduct(row);selectionSummary.Text=$"{products.SelectedItems.Count} ürün seçili · {row.Sku} · {row.RemoteStatus}"+(row.StatusDetail.Length>0?"\n"+row.StatusDetail:"");}else{editingId=null;editorDirty=false;selectionSummary.Text="Ürün kartını açmak için çift tıklayın. Birden fazla ürün için Ctrl / Shift kullanın.";}};
        products.MouseDoubleClick+=(_,e)=>{if(e.OriginalSource is DependencyObject source&&ItemsControl.ContainerFromElement(products,source) is DataGridRow)OpenProductEditor();};
        productSearch.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){productOffset=0;RefreshProducts();}};
        statusFilter.SelectionChanged+=(_,_)=>{productOffset=0;if(loaded)RefreshProducts();};pageSize.SelectionChanged+=(_,_)=>{productOffset=0;if(loaded)RefreshProducts();};mode.SelectionChanged+=(_,_)=>InvalidatePreview();return host;
    }
    async Task RefreshStoreProducts(){var account=Account();var next=store.Load(account.SupplierId);using var client=new TrendyolApiClient(account);var approved=await client.GetProductsAsync(true,Token);var unapproved=await client.GetProductsAsync(false,Token);EnsureAccount(account);next.Products=approved.Concat(unapproved).ToList();next.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(next);Reload();}
    void ShowProductView(string view){if(view=="list"&&!DiscardEditorChanges())return;productListView.Visibility=view=="list"?Visibility.Visible:Visibility.Collapsed;productEditorView.Visibility=view=="editor"?Visibility.Visible:Visibility.Collapsed;productPreviewView.Visibility=view=="preview"?Visibility.Visible:Visibility.Collapsed;productMatchView.Visibility=view=="matching"?Visibility.Visible:Visibility.Collapsed;}
    void OpenProductEditor(){if(products.SelectedItem is not TrendyolProductRow row)throw new InvalidOperationException("Ürün listesinden bir ürün seçin.");EditProduct(row);ShowProductView("editor");}
    bool DiscardEditorChanges(){if(!editorDirty)return true;if(MessageBox.Show(Window.GetWindow(this),"Kaydedilmemiş ürün profili değişiklikleri bırakılsın mı?","Ürün kartı",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return false;if(products.SelectedItem is TrendyolProductRow row)EditProduct(row);return true;}
    FrameworkElement BuildProductEditor()
    {
        integration.Name="TrendyolIntegrationCode";
        foreach(var box in new[]{integration,origin,model,title,description,salePrice,listPrice})box.TextChanged+=(_,_)=>MarkEditorDirty();
        foreach(var combo in new[]{category,brand,delivery})combo.SelectionChanged+=(_,_)=>MarkEditorDirty();
        var form=new StackPanel();
        Field(form,"Gönderilecek barkod (boş: ürün kartındaki barkod)",integration);form.Children.Add(linkedBarcode);form.Children.Add(T("Stok kodu ayrı gönderilir. Barkod değişince eşleştirme ekranında yeniden kontrol edin.",true));Field(form,"Kategori (boş: kategori eşleştirmesi)",category);Field(form,"Marka (boş: marka eşleştirmesi)",brand);
        Field(form,"Model kodu (boş: SKU)",model);Field(form,"Menşei (TR, CN…; 23.10.2026 itibarıyla zorunlu)",origin);Field(form,"Trendyol başlığı (en fazla 100)",title);description.AcceptsReturn=true;description.Height=80;description.TextWrapping=TextWrapping.Wrap;Field(form,"Trendyol açıklaması",description);
        Field(form,"KDV dahil satış fiyatı (TRY)",salePrice);Field(form,"KDV dahil liste fiyatı (TRY)",listPrice);Field(form,"Teslimat şablonu · termin ve kargo",delivery);form.Children.Add(productDesi);
        var attributeForm=new StackPanel();attributeForm.Children.Add(T("* işaretli özellikler zorunludur. Kategori seçince o kategoriye ait özellikler gösterilir.",true));
        attributeForm.Children.Add(A("Kategori özelliklerini yenile",async()=>{var account=Account();var next=store.Load(account.SupplierId);var id=EffectiveCategory();if(id<=0)throw new InvalidOperationException("Kategori seçin veya kategori eşleştirmesini yapın.");using var client=new TrendyolApiClient(account);var values=await client.GetAttributesAsync(id,Token);EnsureAccount(account);next.Attributes[id]=values.ToList();next.AttributesUpdatedUtc[id]=DateTime.UtcNow;store.Save(next);state=store.Load(account.SupplierId);editorRevision=state.Revision;InvalidatePreview();BuildAttributes();status.Text="Güncel kategori özellikleri alındı. * işaretli alanlar zorunludur.";}));
        attributeForm.Children.Add(attributes);form.Children.Add(B("Kategori içerik şablonunu profile al",()=>{
            var row=products.SelectedItem as TrendyolProductRow??throw new InvalidOperationException("Ürün seçin.");var entry=taxonomy.List(TaxonomyKind.Category).SingleOrDefault(e=>e.Active&&TaxonomyStore.SameCategory(e.Name,row.Category))??throw new InvalidOperationException("Yerel kategori bulunamadı.");
            var template=contentTemplates.Get(entry.Id,"trendyol",Account().SupplierId)??throw new InvalidOperationException("Kategoriler → İçerik / özellik şablonu ekranında Trendyol ve satıcı ID'niz için şablon kaydedin.");
            var rendered=contentTemplates.Preview(template,row.Product);title.Text=rendered.Name;description.Text=rendered.Description;status.Text="Başlık/açıklama kategori şablonundan alındı. İnceleyip profili kaydedin; özellikleri güncel API listesinden seçin.";
        }));form.Children.Add(B("Şablonu seçili ürünlere ata",AssignTemplate));category.SelectionChanged+=(_,_)=>{if(!populating){BuildAttributes();InvalidatePreview();}};
        var dock=new DockPanel();var heading=new StackPanel();editTitle.FontSize=17;editTitle.FontWeight=FontWeights.SemiBold;heading.Children.Add(editTitle);heading.Children.Add(T("Boş fiyat, başlık ve açıklama yerel üründen alınır. Kaydetmek yalnız Trendyol profilini değiştirir.",true));DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);var save=Primary(B("Ürün Trendyol profilini kaydet",SaveProductProfile));save.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(save,Dock.Bottom);dock.Children.Add(save);dock.Children.Add(Columns(Section("Ürün ve gönderim bilgileri",Scroll(form)),Section("Kategori özellikleri",Scroll(attributeForm))));return dock;
    }
    void RefreshProducts()
    {
        InvalidatePreview();var profiles=state.Profiles.ToDictionary(p=>p.ProductId);var remotes=state.Products.GroupBy(p=>p.Barcode).ToDictionary(g=>g.Key,g=>g.First());
        var activities=state.SellerId.Length>0?store.ProductActivities(state.SellerId):new Dictionary<string,TrendyolProductActivity>();
        var rows=catalog.Products().Select(p=>{profiles.TryGetValue(p.Id,out var profile);TrendyolRemoteProduct? remote=null;if(profile!=null)remotes.TryGetValue(profile.IntegrationCode,out remote);
            var key=remote==null?(string.IsNullOrEmpty(profile?.IntegrationCode)?"unmatched":"missing"):string.IsNullOrEmpty(remote.Status)?remote.Approved?"approved":"unapproved":remote.Status;
            var categoryId=profile?.CategoryId??state.Mappings.FirstOrDefault(m=>m.Kind==TaxonomyKind.Category&&TaxonomyStore.SameCategory(m.LocalName,p.Category))?.RemoteId;var brandId=profile?.BrandId??state.Mappings.FirstOrDefault(m=>m.Kind==TaxonomyKind.Brand&&TrendyolMatching.Normalize(m.LocalName)==TrendyolMatching.Normalize(p.Brand))?.RemoteId;
            var categoryFound=categoryId.HasValue&&state.Categories.Any(c=>c.Id==categoryId&&c.IsLeaf);var brandFound=brandId.HasValue&&state.Brands.Any(b=>b.Id==brandId);
            activities.TryGetValue(p.Id,out var activity);var createMessage=remote is {Approved:false}?(remote.StatusDetail.Length>0?remote.StatusDetail:StatusLabel(key)):activity?.CreateMessage??"";
            return new TrendyolProductRow(p,profile?.IntegrationCode??"",StatusLabel(key),remote?.SalePrice,remote?.Quantity){StatusKey=key,StatusDetail=remote?.StatusDetail??"",Approved=remote?.Approved??false,CategoryId=categoryFound?categoryId:null,RemoteListPrice=remote?.ListPrice,CreateMessage=createMessage,UpdateMessage=activity?.UpdateMessage??"",MappingIncomplete=!categoryFound||!brandFound,MappingSummary=(categoryFound?$"K: {categoryId}":"Kategori eksik")+" · "+(brandFound?$"M: {brandId}":"Marka eksik")};}).ToList();
        var query=productSearch.Text.Trim();var filtered=rows.Where(r=>MatchesStatus(r,(string?)statusFilter.SelectedItem??"Tümü")&&MatchesDetailedFilters(r)&&(query.Length==0||new[]{r.Sku,r.Name,r.Barcode,r.Gtin,r.IntegrationCode,r.Category,r.Brand}.Any(v=>v.Contains(query,StringComparison.CurrentCultureIgnoreCase)))).ToList();if(productOffset>=filtered.Count)productOffset=0;var page=filtered.Skip(productOffset).Take(PageSize).ToList();
        products.ItemsSource=page;productCount.Text=$"{(page.Count==0?0:productOffset+1)}–{productOffset+page.Count} / {filtered.Count:N0}";controlSummary.Text=$"Son kontrol: {Time(state.ProductsUpdatedUtc)} · {rows.Count(r=>r.StatusKey=="onSale")} yayında · {rows.Count(r=>r.StatusKey=="pendingApproval")} onay bekliyor · {rows.Count(r=>r.StatusKey=="rejected")} reddedildi";editingId=null;editorDirty=false;editTitle.Text="Ürün seçin";
    }
    static string StatusLabel(string key)=>key switch{"onSale"=>"Yayında","pendingApproval"=>"Onay bekliyor","rejected"=>"Reddedildi","documentRequired"=>"Belge gerekli","unmatched"=>"Eşleşmedi","approved"=>"Onaylı","notOnSale"=>"Satışta değil","archived"=>"Arşivde","locked"=>"Kilitli","blacklisted"=>"Satışa kapatılmış","missing"=>"Mağazada bulunamadı",_=>"Durum yenilenmeli"};
    static bool MatchesStatus(TrendyolProductRow row,string label)=>label=="Tümü"||(label=="Onaylı"?row.Approved:label=="Eksik eşleştirme"?row.MappingIncomplete:row.RemoteStatus==label);
    void EditProduct(TrendyolProductRow row)
    {
        populating=true;try{
            editingId=row.Id;editorRevision=state.Revision;var profile=state.Profiles.SingleOrDefault(p=>p.ProductId==row.Id)??new(){ProductId=row.Id};editTitle.Text=row.Sku+" · "+row.Name;
            integration.Text=profile.ListingBarcode;linkedBarcode.Text=$"Ürün barkodu: {(row.Barcode.Length>0?row.Barcode:"girilmemiş")} · Mağazadaki eşleşme: {(profile.IntegrationCode.Length>0?profile.IntegrationCode:"yok")}";productDesi.Text="Ürün kartından desi: "+row.Product.XmlAttributes.GetValueOrDefault("Desi","girilmemiş")+". Ürün yönetimindeki ürün kartından veya Excel ile düzenleyebilirsiniz.";model.Text=profile.ModelCode;origin.Text=profile.Origin;title.Text=profile.Title;description.Text=profile.Description;salePrice.Text=profile.SalePriceTry?.ToString(CultureInfo.CurrentCulture)??"";listPrice.Text=profile.ListPriceTry?.ToString(CultureInfo.CurrentCulture)??"";
            category.ItemsSource=new[]{new TrendyolCategory(0,"","(Kategori eşleştirmesini kullan)",true)}.Concat(state.Categories.Where(c=>c.IsLeaf)).ToList();category.SelectedItem=category.Items.Cast<TrendyolCategory>().FirstOrDefault(c=>c.Id==(profile.CategoryId??0));
            brand.ItemsSource=new[]{new TrendyolBrand(0,"(Marka eşleştirmesini kullan)")}.Concat(state.Brands).ToList();brand.SelectedItem=brand.Items.Cast<TrendyolBrand>().FirstOrDefault(b=>b.Id==(profile.BrandId??0));
            delivery.ItemsSource=new[]{new TrendyolDeliveryTemplate("","(Mağaza varsayılanı)","",null,null,null)}.Concat(state.Templates).ToList();delivery.SelectedItem=delivery.Items.Cast<TrendyolDeliveryTemplate>().FirstOrDefault(t=>t.Id==profile.DeliveryTemplateId);
            BuildAttributes();
        }finally{populating=false;editorDirty=false;}
    }
    void MarkEditorDirty(){if(populating||editingId==null)return;editorDirty=true;InvalidatePreview();status.Text="Trendyol profilinde kaydedilmemiş değişiklik var. Önizlemeden önce profili kaydedin.";}
    long EffectiveCategory()=>category.SelectedItem is TrendyolCategory {Id:>0} selected?selected.Id:products.SelectedItem is TrendyolProductRow row?state.Mappings.FirstOrDefault(m=>m.Kind==TaxonomyKind.Category&&TaxonomyStore.SameCategory(m.LocalName,row.Category))?.RemoteId??0:0;
    void BuildAttributes()
    {
        attributes.Children.Clear();attributeInputs.Clear();if(editingId==null)return;
        var categoryId=EffectiveCategory();if(!state.Attributes.TryGetValue(categoryId,out var definitions)){attributes.Children.Add(T("Özellikler henüz alınmadı. Kategori özelliklerini yenile düğmesini kullanın.",true));return;}
        var profile=state.Profiles.SingleOrDefault(p=>p.ProductId==editingId);
        var brandName=(products.SelectedItem as TrendyolProductRow)?.Brand??"";var safety=state.BrandSafetyTemplates.SingleOrDefault(t=>TrendyolMatching.Normalize(t.BrandName)==TrendyolMatching.Normalize(brandName));
        foreach(var def in definitions.OrderByDescending(d=>d.Required).ThenBy(d=>d.Name))
        {
            attributes.Children.Add(T((def.Required?"* ":"")+def.Name));var selected=profile?.Attributes.SingleOrDefault(a=>a.AttributeId==def.Id);var custom=Box();custom.Text=selected?.CustomValue??"";
            if(selected is null&&safety?.Values.TryGetValue(def.Id,out var inherited)==true)attributes.Children.Add(T("Marka şablonundan: "+inherited+" (burayı doldurursanız ürünün özel değeri kullanılır)",true));
            if(def.AllowMultiple){var list=new ListBox{ItemsSource=def.Values,DisplayMemberPath="Name",SelectionMode=SelectionMode.Multiple,MaxHeight=130,Margin=new(3)};foreach(var value in def.Values.Where(v=>selected?.ValueIds.Contains(v.Id)==true))list.SelectedItems.Add(value);attributes.Children.Add(list);list.SelectionChanged+=(_,_)=>MarkEditorDirty();if(def.AllowCustom){attributes.Children.Add(T("Veya serbest değer"));attributes.Children.Add(custom);}attributeInputs[def.Id]=()=>list.SelectedItems.Count==0&&custom.Text.Length==0?null:new(def.Id,list.SelectedItems.Cast<TrendyolAttributeValue>().Select(v=>v.Id).ToArray(),custom.Text.Trim());}
            else{var combo=Combo();combo.ItemsSource=new[]{new TrendyolAttributeValue(0,"(Seçilmedi)")}.Concat(def.Values).ToList();combo.SelectedItem=combo.Items.Cast<TrendyolAttributeValue>().FirstOrDefault(v=>v.Id==(selected?.ValueIds.FirstOrDefault()??0));attributes.Children.Add(combo);combo.SelectionChanged+=(_,_)=>MarkEditorDirty();if(def.AllowCustom){attributes.Children.Add(T("Veya serbest değer"));attributes.Children.Add(custom);}attributeInputs[def.Id]=()=>combo.SelectedItem is TrendyolAttributeValue {Id:>0} value?new(def.Id,[value.Id],custom.Text.Trim()):custom.Text.Length>0?new(def.Id,[],custom.Text.Trim()):null;}
            custom.TextChanged+=(_,_)=>MarkEditorDirty();
        }
    }
    void SaveProductProfile()
    {
        var id=editingId??throw new InvalidOperationException("Ürün seçin.");var next=store.Load(Account().SupplierId);if(next.Revision!=editorRevision)throw new InvalidOperationException("Profil eskidi; ürünü yeniden seçin.");
        var profile=next.Profiles.SingleOrDefault(p=>p.ProductId==id)??new(){ProductId=id};profile.ListingBarcode=integration.Text.Trim();profile.ModelCode=model.Text.Trim();profile.Origin=origin.Text.Trim().ToUpperInvariant();profile.Title=title.Text.Trim();profile.Description=description.Text.Trim();profile.SalePriceTry=OptionalDecimal(salePrice.Text);profile.ListPriceTry=OptionalDecimal(listPrice.Text);profile.CategoryId=category.SelectedItem is TrendyolCategory {Id:>0} c?c.Id:null;profile.BrandId=brand.SelectedItem is TrendyolBrand {Id:>0} b?b.Id:null;profile.DeliveryTemplateId=(delivery.SelectedItem as TrendyolDeliveryTemplate)?.Id??"";
        profile.Attributes=attributeInputs.Values.Select(read=>read()).OfType<TrendyolAttributeSelection>().ToList();next.Profiles.RemoveAll(p=>p.ProductId==id);next.Profiles.Add(profile);store.Save(next);Reload();var row=products.Items.Cast<TrendyolProductRow>().FirstOrDefault(r=>r.Id==id);products.SelectedItem=row;status.Text="Trendyol profili kaydedildi. Yerel ürün alanları korunuyor.";
    }
    static decimal? OptionalDecimal(string text){if(string.IsNullOrWhiteSpace(text))return null;if(!decimal.TryParse(text,NumberStyles.Number,CultureInfo.CurrentCulture,out var value))throw new InvalidOperationException("Sayı biçimi geçersiz.");return value;}
    string[] SelectedProductIds(){var ids=products.SelectedItems.Cast<TrendyolProductRow>().Select(p=>p.Id).ToArray();if(ids.Length==0)throw new InvalidOperationException("Ürünler listesinden en az bir ürün seçin.");return ids;}
    void BuildPreview()
    {
        if(editorDirty)throw new InvalidOperationException("Önce ürün Trendyol profilindeki değişiklikleri kaydedin.");
        PresentPreview(store.Preview(Account(),SelectedProductIds(),((Mode)mode.SelectedItem).Value));
    }
    void PresentPreview(TrendyolPlan next){plan=next;preview.ItemsSource=plan.Rows;payload.Text=JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(plan.PayloadJson),new JsonSerializerOptions{WriteIndented=true});send.IsEnabled=plan.Rows.Any(r=>r.ItemJson!=null)&&!plan.Rows.Any(r=>r.Status=="Hatalı");status.Text=$"Satıcı {plan.SellerId} · {ModeLabel(plan.Operation)} · {plan.Rows.Count(r=>r.ItemJson!=null)} gönderilecek · {plan.Rows.Count(r=>r.Status=="Hatalı")} hatalı · 15 dakika geçerli önizleme";previewSummary.Text=status.Text;ShowProductView("preview");}
    void AssignTemplate()=>AssignTemplate(delivery.SelectedItem as TrendyolDeliveryTemplate);
    void AssignTemplate(TrendyolDeliveryTemplate? selectedTemplate)
    {
        if(editorDirty)throw new InvalidOperationException("Şablon atamadan önce ürün kartındaki değişiklikleri kaydedin.");
        var ids=SelectedProductIds();var template=selectedTemplate??throw new InvalidOperationException("Şablon seçin.");
        if(MessageBox.Show(Window.GetWindow(this),$"{ids.Length} ürüne '{template.Name}' atanacak. Kargo/adres ve süre gönderimlerini ayrı ayrı önizleyebilirsiniz.","Toplu şablon ataması",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        var next=CurrentDeliveryState();if(template.Id.Length>0&&!next.Templates.Any(t=>t.Id==template.Id))throw new InvalidOperationException("Şablon değişti; yeniden seçin.");
        foreach(var id in ids){var profile=next.Profiles.SingleOrDefault(p=>p.ProductId==id);if(profile is null){profile=new(){ProductId=id};next.Profiles.Add(profile);}profile.DeliveryTemplateId=template.Id;}
        store.Save(next);state=store.Load(next.SellerId);InvalidatePreview();RefreshTemplates(state.Templates.FirstOrDefault(t=>t.Id==template.Id));RefreshDeliveryPickers(template.Id);
        if(products.SelectedItem is TrendyolProductRow row)EditProduct(row);
        status.Text=$"'{template.Name}' {ids.Length} ürüne atandı. Ürün seçimi korundu; teslimat süresini veya kargo/adresleri önizleyebilirsiniz.";
    }
}
