using ClosedXML.Excel;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>One file / mapping / preview / apply workflow for each local Excel destination.</summary>
public sealed class ExcelWorkspacePanel : Grid
{
    readonly CatalogStore catalog;
    readonly TaxonomyStore taxonomy;
    readonly OrdersStore orders;
    readonly ExcelProfileStore profiles;
    readonly string kind;
    readonly Action changed;
    readonly IReadOnlyList<ExcelProductField> fields;
    readonly StackPanel editor=new();
    readonly TextBox file=new(){Name="ExcelFilePath",MinWidth=180,Width=340};
    readonly TextBox header=new(){Text="1",Width=45};
    readonly TextBox culture=new(){Text="tr-TR",Width=85};
    readonly TextBox profileName=new(){Text="Yeni profil",Width=150};
    readonly TextBox market=new(){Text="manual",Width=110};
    readonly TextBox shop=new(){Text="default",Width=120};
    readonly ComboBox sheets=new(){Width=130};
    readonly ComboBox savedProfiles=new(){Width=180,DisplayMemberPath="Name"};
    readonly ComboBox mode=new(){Name="ExcelMode",Width=235,DisplayMemberPath="Label",SelectedValuePath="Mode"};
    readonly CheckBox vat=new(){Content="Fiyatlar KDV dâhil",IsChecked=true};
    readonly CheckBox addStock=new(){Content="Adet ekle (mevcut stoğa topla)"};
    readonly Dictionary<string,ComboBox> mappings=new(StringComparer.OrdinalIgnoreCase);
    readonly DataGrid previewGrid=new(){Name="ExcelPreviewRows",AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,MinHeight=120,EnableRowVirtualization=true};
    readonly TextBlock status=Text("Bir dosya yükleyin; sütunları eşleyip önizleme oluşturun.");
    readonly TextBlock detail=Text("");
    readonly Button apply=new(){Name="ApplyExcel",Content="4. Seçili satırları uygula",IsEnabled=false};
    readonly Button previewButton=new(){Name="PreviewExcel",Content="3. Önizleme oluştur"};
    readonly Button undo=new(){Content="Son işlemi geri al"};
    ExcelImportProfile profile;
    ExcelProductPlan? productPlan;
    ExcelAuxiliaryPlan? otherPlan;
    IReadOnlyList<ExcelSheetColumn> columns=Array.Empty<ExcelSheetColumn>();
    string? loadedPath;
    bool loading;
    bool busy;
    int revision;
    sealed record ModeChoice(ExcelImportMode Mode,string Label);

    public ExcelWorkspacePanel(CatalogStore catalog,string? directory,string kind,Action changed)
    {
        if(kind is not ("products" or "categories" or "orders"))throw new ArgumentException("Excel işlem hedefi geçersiz.");
        this.catalog=catalog;this.kind=kind;this.changed=changed;
        taxonomy=new TaxonomyStore(directory);orders=new OrdersStore(directory);profiles=new ExcelProfileStore(directory);
        profile=new ExcelImportProfile{DataKind=kind,CultureName="tr-TR"};
        fields=kind=="products"?ExcelProductImport.Fields:kind=="orders"?ExcelOrderImport.Fields:new[]{new ExcelProductField("Category","Kategori ağacı","Kategoriler")};
        Margin=new Thickness(8);
        RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});RowDefinitions.Add(new(){Height=GridLength.Auto});
        var settingsScroll=new ScrollViewer{Content=editor,MaxHeight=380,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Children.Add(settingsScroll);
        var fileBar=new WrapPanel();editor.Children.Add(fileBar);
        fileBar.Children.Add(Text("1. Dosya",true));fileBar.Children.Add(file);
        fileBar.Children.Add(ActionButton("Gözat…",()=>{var picker=new OpenFileDialog{Filter="Excel çalışma kitabı (*.xlsx)|*.xlsx"};if(picker.ShowDialog(Window.GetWindow(this))==true)LoadWorkbook(picker.FileName);}));
        fileBar.Children.Add(ActionButton("Dosyayı yükle",()=>LoadWorkbook(file.Text)));
        fileBar.Children.Add(Text("Sayfa"));fileBar.Children.Add(sheets);fileBar.Children.Add(Text("Başlık satırı"));fileBar.Children.Add(header);
        var profileBar=new WrapPanel();profileBar.Children.Add(savedProfiles);profileBar.Children.Add(profileName);profileBar.Children.Add(Text("Sayı biçimi"));profileBar.Children.Add(culture);
        profileBar.Children.Add(ActionButton("Profili kaydet",()=>{Capture();profile.Name=profileName.Text.Trim();profiles.Save(profile);ReloadProfiles();status.Text="Eşlemeler ve işlem seçenekleri kaydedildi.";}));
        profileBar.Children.Add(ActionButton("Yeni profil",()=>{profile=new(){DataKind=kind,CultureName="tr-TR"};ShowProfile();Invalidate();}));
        editor.Children.Add(new Expander{Header="Kaydedilmiş eşlemeler ve sayı biçimi",Content=profileBar,Margin=new Thickness(3)});
        var options=new WrapPanel();editor.Children.Add(options);options.Children.Add(Text("2. İşlem ve eşleme",true));
        if(kind=="products")
        {
            mode.ItemsSource=new[]{new ModeChoice(ExcelImportMode.AddAndUpdate,"Yeni ekle + mevcut ürünü güncelle"),new ModeChoice(ExcelImportMode.AddOnly,"Yalnız yeni ürün ekle"),new ModeChoice(ExcelImportMode.UpdateOnly,"Mevcut ürünü güncelle"),new ModeChoice(ExcelImportMode.PriceOnly,"Yalnız fiyat güncelle"),new ModeChoice(ExcelImportMode.StockOnly,"Yalnız stok / adet güncelle")};
            mode.SelectedValue=profile.ImportMode;options.Children.Add(mode);options.Children.Add(vat);options.Children.Add(addStock);
        }
        else if(kind=="orders"){options.Children.Add(Text("Pazaryeri"));options.Children.Add(market);options.Children.Add(Text("Mağaza"));options.Children.Add(shop);options.Children.Add(vat);}
        var hint=Text(kind switch{"products"=>"Eşleşme: stok kodu / SKU. Dolu hücreler güncellenir; eşlenmeyen, boş ve XML kilitli alanlar korunur. Aynı sütunu birden fazla fiyata atayabilirsiniz.","categories"=>"Kategori yolunu tek sütunda yazın: Giyim > Erkek > Tişört. Üst kategoriler de oluşturulur.",_=>"Aynı sipariş numarasının ürünlerini ayrı satırlara yazın. Siparişin tüm satırları birlikte uygulanır; katalog stoğu otomatik düşmez."});editor.Children.Add(hint);
        var tabs=new TabControl{Height=210,Margin=new Thickness(0,3,0,5)};editor.Children.Add(tabs);
        foreach(var group in fields.GroupBy(f=>f.Group))
        {
            var items=new WrapPanel();
            foreach(var field in group)
            {
                var row=new DockPanel{Width=440,Margin=new Thickness(0,2,10,2)};
                var label=Text(field.Label);label.Width=185;row.Children.Add(label);
                var combo=new ComboBox{Name="Map_"+field.Key.Replace(':','_'),IsEditable=true,IsTextSearchEnabled=false,MinWidth=180,DisplayMemberPath="Label",SelectedValuePath="Letter",ToolTip="Sütun harfi yazın veya listeden seçin: A, B, C … AA"};mappings[field.Key]=combo;AutomationProperties.SetName(combo,field.Label+" Excel sütunu");row.Children.Add(combo);items.Children.Add(row);
                combo.AddHandler(TextBoxBase.TextChangedEvent,new TextChangedEventHandler((_,_)=>Invalidate()));
            }
            tabs.Items.Add(new TabItem{Header=group.Key,Content=new ScrollViewer{Content=items,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        }
        var actions=new WrapPanel{Margin=new Thickness(0,3,0,5)};SetRow(actions,1);Children.Add(actions);actions.Children.Add(previewButton);actions.Children.Add(ActionButton("Geçerli satırları seç",SelectWritable));actions.Children.Add(apply);actions.Children.Add(undo);
        actions.Children.Add(ActionButton("Hata raporu",ExportErrors));actions.Children.Add(ActionButton("Örnek Excel",ExportTemplate));
        if(kind=="products")actions.Children.Add(ActionButton("Ürünleri dışa aktar",ExportProducts));
        previewButton.Click+=async(_,_)=>await GeneratePreviewAsync();apply.Click+=async(_,_)=>await ApplyAsync();undo.Click+=async(_,_)=>await UndoAsync();
        SetRow(previewGrid,2);Children.Add(previewGrid);
        AddColumn("Satır","RowNumber",55);AddColumn("Sonuç","Status",120);
        if(kind=="products"){AddColumn("Stok kodu / SKU","Sku",130);AddColumn("Ürün adı","Name",180);AddColumn("Eski fiyat","OldPrice",85);AddColumn("Yeni fiyat","NewPrice",85);AddColumn("Eski adet","OldStock",75);AddColumn("Yeni adet","NewStock",75);AddColumn("Değişecek alanlar","Changes",350);}
        else{AddColumn(kind=="orders"?"Sipariş no":"Kategori yolu","Key",220);AddColumn(kind=="orders"?"Ürün":"Kategori","Name",220);}
        AddColumn("Açıklama / korunan alanlar","Note",330);
        var style=new Style(typeof(DataGridRow));foreach(var(action,color)in new[]{("CREATE","#ECF8F0"),("UPDATE","#EDF5FC"),("SKIP","#F3F4F5"),("ERROR","#FDEEEF")}){var trigger=new DataTrigger{Binding=new Binding("Action"),Value=action};trigger.Setters.Add(new Setter(Control.BackgroundProperty,(Brush)new BrushConverter().ConvertFromString(color)!));style.Triggers.Add(trigger);}previewGrid.RowStyle=style;
        var footer=new StackPanel{Margin=new Thickness(0,5,0,0)};SetRow(footer,3);Children.Add(footer);detail.MaxHeight=65;footer.Children.Add(detail);footer.Children.Add(status);
        previewGrid.SelectionChanged+=(_,_)=>{UpdateApply();detail.Text=previewGrid.SelectedItem switch{ExcelProductRow r=>r.Changes+"  "+r.Note,ExcelAuxiliaryRow r=>r.Note,_=>""};};
        file.TextChanged+=(_,_)=>{if(!loading){loadedPath=null;Invalidate();}};
        sheets.SelectionChanged+=(_,_)=>{if(!loading && loadedPath!=null){Invalidate();Safe(()=>LoadColumns(false));}};
        header.TextChanged+=(_,_)=>{if(!loading){Invalidate();if(loadedPath!=null)Safe(()=>LoadColumns(false));}};
        mode.SelectionChanged+=(_,_)=>{UpdateAllowedFields();Invalidate();};vat.Checked+=(_,_)=>Invalidate();vat.Unchecked+=(_,_)=>Invalidate();addStock.Checked+=(_,_)=>Invalidate();addStock.Unchecked+=(_,_)=>Invalidate();culture.TextChanged+=(_,_)=>Invalidate();market.TextChanged+=(_,_)=>Invalidate();shop.TextChanged+=(_,_)=>Invalidate();
        savedProfiles.SelectionChanged+=(_,_)=>{if(loading||savedProfiles.SelectedItem is not ExcelImportProfile saved)return;profile=JsonSerializer.Deserialize<ExcelImportProfile>(JsonSerializer.Serialize(saved))!;ShowProfile();if(loadedPath!=null)Safe(()=>LoadColumns(true));Invalidate();};
        ReloadProfiles();UpdateAllowedFields();UpdateUndo();
        AutomationProperties.SetName(this,kind+" Excel aktarımı");
    }
    static TextBlock Text(string value,bool bold=false)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(4,3,6,3),Foreground=new SolidColorBrush(Color.FromRgb(55,78,88))};
    Button ActionButton(string label,Action action){var b=new Button{Content=label};b.Click+=(_,_)=>Safe(action);return b;}
    void Safe(Action action){try{action();}catch(Exception ex){Invalidate();status.Text=ex.Message;}}
    void AddColumn(string title,string path,int width)=>previewGrid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(path),Width=width,MinWidth=45});
    void Invalidate(){if(loading)return;revision++;productPlan=null;otherPlan=null;previewGrid.ItemsSource=null;detail.Text="";apply.IsEnabled=false;status.Text="Ayarlar değişti. Uygulamadan önce önizleme oluşturun.";}
    void Capture()
    {
        if(!int.TryParse(header.Text,out var number)||number<1)throw new InvalidOperationException("Başlık satırı pozitif tam sayı olmalı.");
        profile.HeaderRow=number;profile.SheetName=sheets.SelectedItem as string;profile.CultureName=culture.Text.Trim();_=ExcelProfileStore.Culture(profile.CultureName);
        profile.DataKind=kind;profile.ImportMode=mode.SelectedValue is ExcelImportMode value?value:ExcelImportMode.UpdateOnly;profile.PriceIncludesVat=vat.IsChecked==true;profile.AddStock=addStock.IsChecked==true;
        profile.UseColumnLetters=true;profile.ColumnLetters=mappings.Where(p=>MappingLetter(p.Value).Length>0).ToDictionary(p=>p.Key,p=>MappingLetter(p.Value),StringComparer.OrdinalIgnoreCase);
        profile.SelectedFields=null;
    }
    static string MappingLetter(ComboBox combo)=>combo.SelectedItem is ExcelSheetColumn col && combo.Text==col.Label?col.Letter:combo.Text.Trim().ToUpperInvariant();
    void ReloadProfiles(){loading=true;try{savedProfiles.ItemsSource=profiles.List().Where(p=>p.DataKind==kind).ToList();savedProfiles.SelectedItem=((IEnumerable<ExcelImportProfile>)savedProfiles.ItemsSource).FirstOrDefault(p=>p.Id==profile.Id);}finally{loading=false;}}
    void ShowProfile()
    {
        loading=true;try{profileName.Text=profile.Name;culture.Text=profile.CultureName;header.Text=profile.HeaderRow.ToString();mode.SelectedValue=profile.ImportMode;vat.IsChecked=profile.PriceIncludesVat;addStock.IsChecked=profile.AddStock;if(profile.SheetName!=null&&sheets.Items.Contains(profile.SheetName))sheets.SelectedItem=profile.SheetName;
            foreach(var f in fields){mappings[f.Key].SelectedItem=null;mappings[f.Key].Text=profile.ColumnLetters.GetValueOrDefault(f.Key,"");}
        }finally{loading=false;}UpdateAllowedFields();
    }
    void UpdateAllowedFields()
    {
        if(kind!="products")return;var value=mode.SelectedValue is ExcelImportMode m?m:ExcelImportMode.UpdateOnly;
        foreach(var field in fields){var enabled=ExcelProductImport.Allowed(value,field.Key);mappings[field.Key].IsEnabled=enabled;}
        addStock.IsEnabled=value!=ExcelImportMode.PriceOnly;vat.IsEnabled=value!=ExcelImportMode.StockOnly;
    }
    public void LoadWorkbook(string path)
    {
        Invalidate();loadedPath=null;
        var full=Path.GetFullPath(path.Trim());var names=CatalogExcel.ListWorksheets(full);
        loading=true;try{file.Text=full;sheets.ItemsSource=names;sheets.SelectedItem=profile.SheetName!=null&&names.Contains(profile.SheetName)?profile.SheetName:names[0];loadedPath=full;}finally{loading=false;}
        try{LoadColumns(true);status.Text="Dosya yüklendi. Harfleri ve işlem türünü kontrol edip önizleme oluşturun.";}catch{loadedPath=null;throw;}
    }
    void LoadColumns(bool suggest)
    {
        if(loadedPath==null)return;
        if(!int.TryParse(header.Text,out var row)||row<1)throw new InvalidOperationException("Başlık satırı geçersiz.");
        var settings=new ExcelImportProfile{SheetName=sheets.SelectedItem as string,HeaderRow=row};columns=ExcelProductImport.Columns(loadedPath,settings);
        loading=true;try
        {
            foreach(var field in fields)
            {
                var combo=mappings[field.Key];var letter=MappingLetter(combo);
                if(profile.ColumnLetters.TryGetValue(field.Key,out var saved)&&letter.Length==0)letter=saved;
                if(suggest && letter.Length==0 && !profile.UseColumnLetters)
                {
                    var aliases=field.Key switch{"Sku"=>new[]{"sku","stok kodu","stokkodu","kod","stockcode"},"Name"=>new[]{"ürün","ürün adı","name","title"},"Price"=>new[]{"fiyat","satış","price"},"Stock" or "Quantity"=>new[]{"stok","adet","stock","quantity"},"OrderId"=>new[]{"sipariş","sipariş no","sipariş numarası","orderid"},"Category"=>new[]{"kategori","kategori ağacı","category"},_=>new[]{field.Key,field.Label}};
                    var match=columns.FirstOrDefault(c=>aliases.Any(a=>ExcelColumnMapping.Normalize(a)==ExcelColumnMapping.Normalize(c.Header)));letter=match?.Letter??"";
                    if(profile.ColumnMappings.TryGetValue(field.Key,out var oldHeader))letter=columns.FirstOrDefault(c=>c.Header==oldHeader)?.Letter??letter;
                }
                combo.ItemsSource=columns;combo.SelectedItem=columns.FirstOrDefault(c=>c.Letter==letter);if(combo.SelectedItem==null)combo.Text=letter;
            }
        }finally{loading=false;}
    }
    public async Task GeneratePreviewAsync()
    {
        if(busy)return;
        try
        {
            if(loadedPath==null)throw new InvalidOperationException("Önce dosyayı yükleyin.");
            Capture();var settings=JsonSerializer.Deserialize<ExcelImportProfile>(JsonSerializer.Serialize(profile))!;var path=loadedPath;var scopeMarket=market.Text;var scopeShop=shop.Text;
            Invalidate();var version=revision;SetBusy(true);status.Text="Excel okunuyor; satır eşleşmeleri ve değişiklikler hesaplanıyor…";
            if(kind=="products"){var result=await Task.Run(()=>ExcelProductImport.Preview(catalog,path,settings));if(version!=revision)return;productPlan=result;previewGrid.ItemsSource=result.Rows;}
            else{var result=await Task.Run(()=>kind=="categories"?ExcelCategoryImport.Preview(taxonomy,path,settings):ExcelOrderImport.Preview(orders,path,settings,scopeMarket,scopeShop));if(version!=revision)return;otherPlan=result;previewGrid.ItemsSource=result.Rows;}
            var actions=productPlan?.Rows.Select(r=>r.Action)??otherPlan!.Rows.Select(r=>r.Action);var list=actions.ToList();status.Text=$"{list.Count} satır · {list.Count(a=>a=="CREATE")} eklenecek · {list.Count(a=>a=="UPDATE")} güncellenecek · {list.Count(a=>a=="SKIP")} atlanacak · {list.Count(a=>a=="ERROR")} hatalı. Uygulanacak satırları seçin.";
            if(list.Contains("ERROR"))status.Text+=" Hatalar düzeltilmeden veri yazılmaz.";
        }
        catch(Exception ex){Invalidate();status.Text="Önizleme oluşturulamadı: "+ex.Message;}
        finally{SetBusy(false);}
    }
    void SelectWritable(){previewGrid.SelectedItems.Clear();foreach(var row in previewGrid.Items)if(row is ExcelProductRow p&&p.CanApply || row is ExcelAuxiliaryRow a&&a.CanApply)previewGrid.SelectedItems.Add(row);}
    int[] SelectedRows()=>previewGrid.SelectedItems.Cast<object>().Select(o=>o switch{ExcelProductRow r when r.CanApply=>r.RowNumber,ExcelAuxiliaryRow r when r.CanApply=>r.RowNumber,_=>-1}).Where(n=>n>=0).ToArray();
    void UpdateApply()=>apply.IsEnabled=!busy && (productPlan!=null&&productPlan.Errors.Count==0||otherPlan!=null&&otherPlan.Errors.Count==0) && SelectedRows().Length>0;
    void SetBusy(bool value){busy=value;editor.IsEnabled=!value;previewButton.IsEnabled=!value;previewGrid.IsEnabled=!value;UpdateApply();undo.IsEnabled=!value&&LastUndo()!=null;}
    async Task ApplyAsync()
    {
        if(busy)return;try
        {
            Capture();var chosen=SelectedRows();if(chosen.Length==0)throw new InvalidOperationException("Önizlemeden satır seçin.");
            if(MessageBox.Show(Window.GetWindow(this),$"Önizlemede seçilen {chosen.Length} satır yerel kayıtlara uygulanacak. Onaylıyor musunuz?","Excel önizlemesini uygula",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            SetBusy(true);var path=loadedPath!;var settings=JsonSerializer.Deserialize<ExcelImportProfile>(JsonSerializer.Serialize(profile))!;var m=market.Text;var s=shop.Text;
            await Task.Run(()=>{if(kind=="products")ExcelProductImport.Apply(catalog,path,settings,productPlan,chosen);else if(kind=="categories")ExcelCategoryImport.Apply(taxonomy,path,settings,otherPlan,chosen);else ExcelOrderImport.Apply(orders,path,settings,otherPlan,chosen,m,s);});
            Invalidate();changed();status.Text=$"{chosen.Length} satır uygulandı. İşlem kalıcı geri alma kaydına alındı.";
        }catch(Exception ex){Invalidate();status.Text="Uygulama durduruldu: "+ex.Message;}finally{SetBusy(false);UpdateUndo();}
    }
    string? LastUndo()=>kind switch{"products"=>catalog.LastExcelImportId(),"categories"=>taxonomy.LastExcelCategoryImportId(),_=>orders.LastExcelOrderImportId()};
    void UpdateUndo()=>undo.IsEnabled=!busy&&LastUndo()!=null;
    async Task UndoAsync()
    {
        if(busy)return;try{var id=LastUndo()??throw new InvalidOperationException("Geri alınacak işlem yok.");if(MessageBox.Show(Window.GetWindow(this),"Son Excel işlemi geri alınsın mı? Sonradan değişen kayıt varsa işlem durdurulur.","Excel geri alma",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;SetBusy(true);await Task.Run(()=>{if(kind=="products")catalog.UndoExcelImport(id);else if(kind=="categories")taxonomy.UndoExcelCategories(id);else orders.UndoExcelOrders(id);});Invalidate();changed();status.Text="Son Excel işlemi geri alındı.";}catch(Exception ex){status.Text=ex.Message;}finally{SetBusy(false);UpdateUndo();}
    }
    void ExportErrors(){var errors=productPlan?.Errors??otherPlan?.Errors;if(errors==null||errors.Count==0)throw new InvalidOperationException("Önizleme hatası yok.");var save=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName="excel-hatalari.xlsx"};if(save.ShowDialog(Window.GetWindow(this))==true)CatalogExcel.ExportErrors(save.FileName,new ExcelPreview(Array.Empty<CatalogProduct>(),errors));}
    void ExportTemplate()
    {
        var save=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName=kind+"-ornek.xlsx"};if(save.ShowDialog(Window.GetWindow(this))!=true)return;
        using var book=new XLWorkbook();var sheet=book.AddWorksheet("Data");
        var chosen=kind=="categories"?new[]{("Kategori","Giyim > Erkek > Tişört")}:kind=="orders"?new[]{("Sipariş numarası","ORD-001"),("SKU","SKU-001"),("Ürün adı","Örnek ürün"),("Adet","2"),("Birim fiyat","100"),("Müşteri adı soyadı","Örnek müşteri")}:mode.SelectedValue is ExcelImportMode.StockOnly?new[]{("SKU","SKU-001"),("Stok","15")}:mode.SelectedValue is ExcelImportMode.PriceOnly?new[]{("SKU","SKU-001"),("Satış","100"),("Trendyol satış fiyatı","120")}:new[]{("SKU","SKU-001"),("Ürün adı","Örnek ürün"),("Kategori","Giyim > Erkek > Tişört"),("Stok","15"),("Satış","100"),("KDV oranı (%)","20"),("Para birimi","TRY")};
        for(int i=0;i<chosen.Length;i++){sheet.Cell(1,i+1).Value=chosen[i].Item1;sheet.Cell(2,i+1).Style.NumberFormat.Format="@";sheet.Cell(2,i+1).Value=chosen[i].Item2;}sheet.Row(1).Style.Font.Bold=true;sheet.Columns().AdjustToContents();book.SaveAs(save.FileName);status.Text="Örnek Excel kaydedildi; örnek satırı kendi verilerinizle değiştirin.";
    }
    void ExportProducts(){var save=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName="urunler.xlsx"};if(save.ShowDialog(Window.GetWindow(this))==true){CatalogExcel.Export(save.FileName,catalog.Products(),profile.VisibleFields);status.Text="Ürünler Excel'e aktarıldı.";}}
}
