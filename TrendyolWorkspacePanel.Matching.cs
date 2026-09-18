using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolMatchReviewRow(CatalogProduct Product,string CatalogHash,string CurrentBarcode,TrendyolRemoteProduct? Target,string Method,bool Skipped=false,string Conflict="",string RequestedBarcode="",bool CreateIfMissing=false,string InputError="")
{
    public string Id=>Product.Id;
    public string Sku=>Product.Sku;
    public string Name=>Product.Name;
    public string Gtin=>Product.Gtin;
    public string Barcode=>Target?.Barcode??RequestedBarcode;
    public string RemoteName=>Target?.Title??"";
    public string Status=>Skipped?"Atlanacak":Conflict.Length>0?"Çakışma":CreateIfMissing?"Yeni açılacak":Target is null?"Barkod bekliyor":CurrentBarcode==Barcode?"Zaten eşleşmiş":"Hazır";
    public string Reason=>Conflict.Length>0?Conflict:Method;
    public bool CanApply=>Status is "Hazır" or "Yeni açılacak";
}

public sealed partial class TrendyolWorkspacePanel
{
    readonly DockPanel productMatchView=new(){Name="TrendyolProductMatchView",Visibility=Visibility.Collapsed};
    readonly DataGrid matchReview=Grid("TrendyolMatchReview",("Ürün kodu","Sku",82),("Yerel ürün","Name",180),("Durum","Status",105),("Trendyol barkodu","Barcode",120));
    readonly DataGrid matchCandidates=Grid("TrendyolMatchCandidates",("Trendyol barkodu","Barcode",120),("Mağaza ürünü","Title",200),("Stok","Quantity",45),("Fiyat","SalePrice",65),("Stok kodu","StockCode",110));
    readonly TextBox matchSearch=Box();
    readonly TextBox matchBarcode=new(){Name="TrendyolMatchBarcode",Margin=new(3),Padding=new(4)};
    readonly TextBlock matchSummary=T(""),matchSelected=T("Soldan yerel ürün seçin."),matchCandidateCount=T("",true);
    readonly ComboBox matchFilter=FilterChoice("TrendyolMatchFilter","Tümü","Hazır","Yeni açılacak","Barkod bekliyor","Çakışma","Zaten eşleşmiş","Atlanacak");
    readonly Button saveMatches=new(){Name="TrendyolSaveMatches",Content="Eşleşmeleri kaydet",IsEnabled=false,Margin=new(3),Padding=new(10,5,10,5)};
    List<TrendyolMatchReviewRow> matchRows=[];
    TrendyolWorkspaceState matchSnapshot=new();
    string matchAccount="";
    bool refreshingMatchRows;
    bool populatingMatchBarcode,barcodeDraftDirty;
    readonly Dictionary<string,string> barcodeDrafts=new(StringComparer.Ordinal);

    void BuildProductMatching()
    {
        var top=new StackPanel();var actions=new WrapPanel();actions.Children.Add(B("← Ürün listesine dön",()=>ShowProductView("list")));actions.Children.Add(Primary(saveMatches));actions.Children.Add(T("Göster"));actions.Children.Add(matchFilter);top.Children.Add(actions);top.Children.Add(matchSummary);
        top.Children.Add(T("Barkod mağazada varsa eşleşir; yoksa yeni ürün önizlemesine alınır. Boş barkod bekler. Trendyol'a gönderim sonraki önizlemede onaylanır.",true));DockPanel.SetDock(top,Dock.Top);productMatchView.Children.Add(top);
        var body=new System.Windows.Controls.Grid();body.ColumnDefinitions.Add(new(){Width=new(3,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new(2,GridUnitType.Star)});
        var left=new DockPanel();var leftActions=new WrapPanel();var skip=B("Seçili satırı atla",()=>ChangeMatch(null,true));skip.Name="TrendyolSkipMatch";leftActions.Children.Add(skip);DockPanel.SetDock(leftActions,Dock.Bottom);left.Children.Add(leftActions);left.Children.Add(matchReview);body.Children.Add(left);
        var right=new DockPanel{Margin=new(8,0,0,0)};matchSelected.FontWeight=FontWeights.SemiBold;var selectedInfo=Scroll(matchSelected);selectedInfo.MaxHeight=125;DockPanel.SetDock(selectedInfo,Dock.Top);right.Children.Add(selectedInfo);
        var methods=new TabControl{Name="TrendyolMatchMethods"};var barcodeForm=new StackPanel();Field(barcodeForm,"Gönderimde kullanılacak Trendyol barkodu",matchBarcode);var check=Primary(B("Barkodu kontrol et",CheckEnteredBarcode));check.Name="TrendyolCheckBarcode";barcodeForm.Children.Add(check);barcodeForm.Children.Add(T("Mağazada bulunan barkod mevcut ürüne bağlanır. Bulunmayan barkodla yeni ürün hazırlanır. SKU ve GTIN otomatik barkod yerine kullanılmaz.",true));barcodeForm.Children.Add(T("Ürün kartındaki Trendyol barkodu kullanılır; boşsa yerel barkod alınır. Burada girdiğiniz değer yalnız Trendyol profilinde saklanır.",true));methods.Items.Add(new TabItem{Header="Barkodla eşleştir / oluştur",Content=Scroll(barcodeForm)});
        var manual=new DockPanel();var searchTop=new StackPanel();matchSearch.Name="TrendyolMatchSearch";Field(searchTop,"Mağaza ürün adı, barkod veya stok kodu",matchSearch);var searchActions=new WrapPanel();var search=B("Mağazada ara",SearchMatchCandidates);search.Name="TrendyolSearchMatches";searchActions.Children.Add(search);searchActions.Children.Add(B("Yerel ürün adıyla ara",()=>{if(matchReview.SelectedItem is TrendyolMatchReviewRow row){matchSearch.Text=row.Name;SearchMatchCandidates();}}));searchTop.Children.Add(searchActions);searchTop.Children.Add(matchCandidateCount);DockPanel.SetDock(searchTop,Dock.Top);manual.Children.Add(searchTop);
        var choose=Primary(B("Seçili mağaza ürününü bu satıra bağla",()=>ChangeMatch(matchCandidates.SelectedItem as TrendyolRemoteProduct??throw new InvalidOperationException("Sağdaki listeden mağaza ürününü seçin."),false)));choose.Name="TrendyolChooseMatch";choose.IsEnabled=false;DockPanel.SetDock(choose,Dock.Bottom);manual.Children.Add(choose);manual.Children.Add(matchCandidates);methods.Items.Add(new TabItem{Header="Mağazada elle ara",Content=manual});right.Children.Add(methods);System.Windows.Controls.Grid.SetColumn(right,1);body.Children.Add(right);productMatchView.Children.Add(body);
        matchReview.SelectionMode=DataGridSelectionMode.Single;matchCandidates.SelectionMode=DataGridSelectionMode.Single;matchReview.RowHeight=29;matchCandidates.RowHeight=29;matchReview.FrozenColumnCount=1;matchReview.Background=matchCandidates.Background=System.Windows.Media.Brushes.White;
        matchReview.Columns[1].Width=new DataGridLength(1,DataGridLengthUnitType.Star);matchReview.Columns[1].MinWidth=130;matchCandidates.Columns[1].Width=new DataGridLength(1,DataGridLengthUnitType.Star);matchCandidates.Columns[1].MinWidth=145;
        var cellText=new Style(typeof(TextBlock));cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));cellText.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Text"){RelativeSource=new RelativeSource(RelativeSourceMode.Self)}));foreach(var column in matchReview.Columns.Concat(matchCandidates.Columns).OfType<DataGridTextColumn>())column.ElementStyle=cellText;
        var rowStyle=new Style(typeof(DataGridRow));foreach(var pair in new[]{("Hazır","#EAF5ED"),("Yeni açılacak","#E5F2FA"),("Çakışma","#FFE2B9"),("Zaten eşleşmiş","#EDF3F7")}){var trigger=new DataTrigger{Binding=new Binding("Status"),Value=pair.Item1};trigger.Setters.Add(new Setter(Control.BackgroundProperty,(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(pair.Item2)!));rowStyle.Triggers.Add(trigger);}matchReview.RowStyle=rowStyle;
        matchCandidates.SelectionChanged+=(_,_)=>choose.IsEnabled=matchCandidates.SelectedItem!=null&&matchReview.SelectedItem!=null;
        matchReview.SelectionChanged+=(_,_)=>{if(refreshingMatchRows)return;choose.IsEnabled=false;if(matchReview.SelectedItem is not TrendyolMatchReviewRow row){matchSelected.Text="Soldan yerel ürün seçin.";matchCandidates.ItemsSource=null;return;}matchSelected.Text=row.Sku+" · "+row.Name+"\nMevcut bağlantı: "+(row.CurrentBarcode.Length>0?row.CurrentBarcode:"yok")+"\n"+row.Reason+(row.Target is null?"":"\nSeçilen mağaza ürünü: "+row.Barcode+" · "+row.RemoteName);populatingMatchBarcode=true;try{matchBarcode.Text=barcodeDrafts.GetValueOrDefault(row.Id,row.Barcode);}finally{populatingMatchBarcode=false;}UpdateMatchSummary();matchSearch.Text=row.Target?.Barcode??row.Name;SearchMatchCandidates();};
        matchBarcode.TextChanged+=(_,_)=>{if(populatingMatchBarcode)return;if(matchReview.SelectedItem is TrendyolMatchReviewRow row){if(matchBarcode.Text.Trim()!=row.Barcode)barcodeDrafts[row.Id]=matchBarcode.Text;else barcodeDrafts.Remove(row.Id);}UpdateMatchSummary();};
        matchSearch.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter)SearchMatchCandidates();};matchFilter.SelectionChanged+=(_,_)=>{if(!refreshingMatchRows)RefreshMatchRows();};saveMatches.Click+=(_,_)=>{try{SaveProductMatches();}catch(Exception ex){status.Text=Safe(ex);matchSummary.Text=Safe(ex);saveMatches.IsEnabled=false;}};
    }
    void MatchProducts()
    {
        if(editorDirty)throw new InvalidOperationException("Eşleştirmeden önce ürün kartındaki değişiklikleri kaydedin.");
        var selected=products.SelectedItems.Cast<TrendyolProductRow>().ToArray();if(selected.Length==0)throw new InvalidOperationException("Ürün listesinden eşleştirilecek satırları seçin.");
        var account=Account();matchSnapshot=store.Load(account.SupplierId);if(!matchSnapshot.ProductsUpdatedUtc.HasValue)throw new InvalidOperationException("Önce Trendyol kontrol / yenile ile mağaza ürünlerini alın.");matchAccount=TrendyolWorkspaceStore.AccountFingerprint(account);
        barcodeDrafts.Clear();barcodeDraftDirty=false;matchRows=selected.Select(row=>{var current=matchSnapshot.Profiles.SingleOrDefault(p=>p.ProductId==row.Id)?.IntegrationCode??"";var review=new TrendyolMatchReviewRow(row.Product,TrendyolWorkspaceStore.Hash(JsonSerializer.Serialize(row.Product)),current,null,"");return EvaluateBarcode(review,current.Length>0?current:row.Barcode);}).ToList();
        matchFilter.SelectedIndex=0;RefreshMatchRows();ShowProductView("matching");
    }
    TrendyolMatchReviewRow EvaluateBarcode(TrendyolMatchReviewRow row,string text)
    {
        var barcode=text.Trim();var error=barcode.Length>40||barcode.Any(ch=>!char.IsLetterOrDigit(ch)&&ch!='.'&&ch!='-'&&ch!='_')?"Barkod en fazla 40 karakter olmalı; boşluk ve özel işaret kullanılamaz.":"";
        var target=error.Length==0&&barcode.Length>0?matchSnapshot.Products.SingleOrDefault(p=>p.Barcode==barcode):null;
        return row with{Target=target,RequestedBarcode=barcode,CreateIfMissing=barcode.Length>0&&error.Length==0&&target is null,InputError=error,Conflict=error,Skipped=false,Method=error.Length>0?error:barcode.Length==0?"Barkod girin; boş barkodla ürün açılmaz.":target is null?"Barkod mağazada yok; yeni ürün oluşturma önizlemesine alınacak.":"Barkod mağazada bulundu; mevcut ürünle eşleşecek."};
    }
    void CheckEnteredBarcode()
    {
        var row=matchReview.SelectedItem as TrendyolMatchReviewRow??throw new InvalidOperationException("Soldaki listeden ürün seçin.");
        matchRows[matchRows.FindIndex(r=>r.Id==row.Id)]=EvaluateBarcode(row,matchBarcode.Text);barcodeDrafts.Remove(row.Id);RefreshMatchRows(row.Id);
    }
    void RefreshMatchRows(string? selectedId=null)
    {
        selectedId??=(matchReview.SelectedItem as TrendyolMatchReviewRow)?.Id;
        var duplicates=matchRows.Where(r=>!r.Skipped&&r.Barcode.Length>0).GroupBy(r=>r.Barcode).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
        matchRows=matchRows.Select(r=>r with{Conflict=r.Skipped?"":r.InputError.Length>0?r.InputError:r.Barcode.Length==0?"":duplicates.Contains(r.Barcode)?"Aynı barkod birden fazla satırda; birini seçip diğerini atlayın.":matchSnapshot.Profiles.Any(p=>p.ProductId!=r.Id&&p.IntegrationCode==r.Barcode)?"Bu barkod başka bir yerel ürüne bağlı.":""}).ToList();
        refreshingMatchRows=true;try{var visible=matchRows.Where(r=>matchFilter.SelectedIndex<=0||r.Status==(string?)matchFilter.SelectedItem).ToList();matchReview.ItemsSource=visible;}finally{refreshingMatchRows=false;}
        matchReview.SelectedItem=matchReview.Items.Cast<TrendyolMatchReviewRow>().FirstOrDefault(r=>r.Id==selectedId)??matchReview.Items.Cast<TrendyolMatchReviewRow>().FirstOrDefault();
        UpdateMatchSummary();
    }
    void UpdateMatchSummary()
    {
        barcodeDraftDirty=barcodeDrafts.Count>0;
        var matched=matchRows.Count(r=>r.Status=="Hazır");var creates=matchRows.Count(r=>r.Status=="Yeni açılacak");var conflicts=matchRows.Count(r=>r.Status=="Çakışma");saveMatches.IsEnabled=matched+creates>0&&conflicts==0&&!barcodeDraftDirty;
        saveMatches.Content=creates>0?$"{matched} eşleştir · {creates} yeni ürünü önizle":$"{matched} eşleşmeyi kaydet";
        matchSummary.Text=$"{matchRows.Count} seçili ürün   |   {matched} eşleşecek   |   {creates} yeni açılacak   |   {matchRows.Count(r=>r.Status=="Barkod bekliyor")} barkod bekliyor   |   {conflicts} çakışma   |   {matchRows.Count(r=>r.Status=="Zaten eşleşmiş")} zaten eşleşmiş   |   {matchRows.Count(r=>r.Skipped)} atlanacak\nMağaza listesi: {matchSnapshot.Products.Count:N0} ürün · {Time(matchSnapshot.ProductsUpdatedUtc)}"+(barcodeDraftDirty?" · Girdiğiniz barkodu kontrol edin.":"");
    }
    void SearchMatchCandidates()
    {
        var words=TrendyolMatching.Normalize(matchSearch.Text).Split(' ',StringSplitOptions.RemoveEmptyEntries);if(words.Length==0){matchCandidates.ItemsSource=null;matchCandidateCount.Text="Aramak için ürün adı veya kod yazın.";return;}
        var found=matchSnapshot.Products.Where(p=>words.All(w=>new[]{p.Barcode,p.StockCode,p.Title}.Any(v=>TrendyolMatching.Normalize(v).Contains(w,StringComparison.Ordinal)))).ToList();matchCandidates.ItemsSource=found.Take(200).ToList();matchCandidateCount.Text=$"{found.Count:N0} sonuç"+(found.Count>200?" · İlk 200 gösteriliyor; aramayı daraltın.":" · Ad benzerliği otomatik eşleştirme yapmaz.");
    }
    void ChangeMatch(TrendyolRemoteProduct? target,bool skip)
    {
        var row=matchReview.SelectedItem as TrendyolMatchReviewRow??throw new InvalidOperationException("Soldaki listeden yerel ürünü seçin.");var index=matchRows.FindIndex(r=>r.Id==row.Id);matchRows[index]=row with{Target=target,RequestedBarcode=target?.Barcode??"",CreateIfMissing=false,InputError="",Skipped=skip,Method=skip?"Bu satır kaydedilmeyecek; mevcut bağlantısı korunur.":"Mağazadan elle seçildi; kaydetmeden önce iki ürünü karşılaştırın.",Conflict=""};barcodeDrafts.Remove(row.Id);RefreshMatchRows(row.Id);
    }
    void SaveProductMatches()
    {
        var account=Account();if(matchAccount!=TrendyolWorkspaceStore.AccountFingerprint(account)||account.SupplierId!=matchSnapshot.SellerId)throw new InvalidOperationException("Mağaza hesabı değişti; eşleştirmeyi yeniden açın.");
        if(barcodeDraftDirty)throw new InvalidOperationException("Girdiğiniz barkodu önce kontrol edin.");
        if(matchRows.Any(r=>r.Status=="Çakışma"))throw new InvalidOperationException("Önce çakışan eşleşmeleri düzeltin veya atlayın.");
        var choices=matchRows.Where(r=>r.CanApply).Select(r=>new TrendyolProductMatchChoice(r.Id,r.CatalogHash,r.Barcode,r.CreateIfMissing)).ToArray();var selected=matchRows.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);var creates=choices.Where(c=>c.CreateIfMissing).Select(c=>c.ProductId).ToArray();
        store.ApplyProductMatches(account.SupplierId,matchSnapshot.Revision,choices);Reload();foreach(var row in products.Items.Cast<TrendyolProductRow>().Where(r=>selected.Contains(r.Id)))products.SelectedItems.Add(row);
        if(creates.Length>0){mode.SelectedItem=mode.Items.Cast<Mode>().Single(m=>m.Value==TrendyolOperation.Create);PresentPreview(store.Preview(account,creates,TrendyolOperation.Create));previewSummary.Text=$"{choices.Length-creates.Length} ürün mevcut mağaza ürünüyle eşleştirildi. {creates.Length} yeni ürün için aşağıdaki önizlemeyi kontrol edin.\n"+previewSummary.Text;}
        else{ShowProductView("list");status.Text=$"{choices.Length} ürün eşleştirildi. Yerel SKU / barkod / GTIN korundu. Trendyol'a ürün veya güncelleme gönderilmedi.";}
    }
}
