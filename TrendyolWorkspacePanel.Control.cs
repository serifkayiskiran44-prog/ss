using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed partial class TrendyolWorkspacePanel
{
    readonly StackPanel detailedFilters=new(){Name="TrendyolDetailedFilters",Visibility=Visibility.Collapsed};
    readonly WrapPanel bulkActions=new(){Name="TrendyolBulkActions",Visibility=Visibility.Collapsed};
    readonly TreeView categoryFilterTree=new(){Height=164,Margin=new(3)};
    readonly ComboBox brandFilter=FilterChoice("TrendyolBrandFilter","Tümü"),sourceFilter=FilterChoice("TrendyolSourceFilter","Tümü"),currencyFilter=FilterChoice("TrendyolCurrencyFilter","Tümü");
    readonly ComboBox activeFilter=FilterChoice("TrendyolActiveFilter","Tümü","Aktif","Pasif"),stockFilter=FilterChoice("TrendyolStockFilter","Tümü","Stokta var","Stok yok");
    readonly ComboBox linkedFilter=FilterChoice("TrendyolLinkedFilter","Tümü","Trendyol'a bağlı","Eşleşmemiş"),mappingFilter=FilterChoice("TrendyolMappingFilter","Tümü","Kategori eksik","Marka eksik","Kategori / marka eşleşmiş");
    readonly ComboBox descriptionFilter=FilterChoice("TrendyolDescriptionFilter","Tümü","Dolu","Boş"),imageFilter=FilterChoice("TrendyolImageFilter","Tümü","Dolu","Boş");
    readonly ComboBox pageSize=new(){ItemsSource=new[]{100,200,500},SelectedIndex=0,MinWidth=65,Margin=new(3)};
    readonly ComboBox bulkDelivery=Combo();
    string selectedFilterCategory="";
    bool updatingFilterOptions;
    int PageSize=>pageSize.SelectedItem is int size?size:100;
    static ComboBox FilterChoice(string name,params string[] items)=>new(){Name=name,ItemsSource=items,SelectedIndex=0,MinWidth=125,Margin=new(3),VerticalContentAlignment=VerticalAlignment.Center};
    static FrameworkElement ControlGroup(string title,UIElement content,double width)=>new GroupBox{Header=title,Content=content,Width=width,Margin=new(4),Padding=new(5),BorderBrush=Line,BorderThickness=new(1)};
    static void FilterField(Panel panel,string label,UIElement input)
    {
        var row=new DockPanel();var caption=new TextBlock{Text=label,Width=85,VerticalAlignment=VerticalAlignment.Center,Margin=new(2),TextWrapping=TextWrapping.Wrap};DockPanel.SetDock(caption,Dock.Left);row.Children.Add(caption);row.Children.Add(input);panel.Children.Add(row);
    }
    FrameworkElement BuildControlToolbar()
    {
        var top=new StackPanel{Margin=new(0,5,0,0)};
        var toolbar=new WrapPanel();var search=new StackPanel();search.Children.Add(new TextBlock{Text="Arama (Ürün adı, stok kodu, barkod, GTIN)",Margin=new(3,0,0,0),FontSize=11});var searchRow=new WrapPanel();productSearch.Name="TrendyolProductSearch";productSearch.Width=240;productSearch.MinWidth=100;searchRow.Children.Add(productSearch);searchRow.Children.Add(B("Ara",()=>{productOffset=0;RefreshProducts();}));search.Children.Add(searchRow);toolbar.Children.Add(search);
        var toggleFilters=B("⌄ Detaylı filtreleme",()=>{});toggleFilters.Click+=(_,_)=>{detailedFilters.Visibility=detailedFilters.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;toggleFilters.Content=detailedFilters.Visibility==Visibility.Visible?"⌃ Detaylı filtreleme":"⌄ Detaylı filtreleme";};toggleFilters.Name="TrendyolToggleFilters";toggleFilters.VerticalAlignment=VerticalAlignment.Bottom;toolbar.Children.Add(toggleFilters);
        var toggleBulk=B("⌄ Toplu işlemler",()=>{});toggleBulk.Click+=(_,_)=>{bulkActions.Visibility=bulkActions.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;toggleBulk.Content=bulkActions.Visibility==Visibility.Visible?"⌃ Toplu işlemler":"⌄ Toplu işlemler";};toggleBulk.Name="TrendyolToggleBulk";toggleBulk.VerticalAlignment=VerticalAlignment.Bottom;toolbar.Children.Add(toggleBulk);
        var match=B("Barkodla eşleştir / oluştur",MatchProducts);match.Name="TrendyolMatchProducts";match.VerticalAlignment=VerticalAlignment.Bottom;toolbar.Children.Add(match);
        var open=B("Ürün kartını aç",OpenProductEditor);open.Name="TrendyolOpenProduct";open.VerticalAlignment=VerticalAlignment.Bottom;toolbar.Children.Add(open);var refresh=A("Trendyol kontrol / yenile",RefreshStoreProducts);refresh.VerticalAlignment=VerticalAlignment.Bottom;toolbar.Children.Add(refresh);top.Children.Add(toolbar);
        var quick=new WrapPanel();quick.Children.Add(T("Seçili ürünlerde"));quick.Children.Add(mode);var previewButton=Primary(B("Önizle",BuildPreview));previewButton.Name="TrendyolBuildPreview";quick.Children.Add(previewButton);controlSummary.FontSize=11;controlSummary.Margin=new(9,6,5,4);quick.Children.Add(controlSummary);top.Children.Add(quick);
        toggleBulk.Click+=(_,_)=>quick.Visibility=bulkActions.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        BuildDetailedFilters();BuildBulkActions();top.Children.Add(detailedFilters);top.Children.Add(bulkActions);
        var viewport=Scroll(top);viewport.Name="TrendyolControlViewport";viewport.MaxHeight=160;SizeChanged+=(_,_)=>viewport.MaxHeight=Math.Clamp(ActualHeight-440,120,260);return viewport;
    }
    void BuildDetailedFilters()
    {
        var groups=new WrapPanel();var categories=new StackPanel();categories.Children.Add(B("Tüm kategoriler",()=>{selectedFilterCategory="";if(categoryFilterTree.SelectedItem is TreeViewItem item)item.IsSelected=false;productOffset=0;RefreshProducts();}));categories.Children.Add(categoryFilterTree);groups.Children.Add(ControlGroup("Kategoriye göre filtreleme",categories,260));
        categoryFilterTree.SelectedItemChanged+=(_,_)=>{if(!updatingFilterOptions)selectedFilterCategory=(categoryFilterTree.SelectedItem as TreeViewItem)?.Tag as string??"";};
        var detail=new StackPanel();FilterField(detail,"Bağlantı",linkedFilter);FilterField(detail,"Eşleştirme",mappingFilter);FilterField(detail,"Açıklama",descriptionFilter);FilterField(detail,"Görseller",imageFilter);groups.Children.Add(ControlGroup("Filtre detayı",detail,245));
        var filters=new StackPanel();FilterField(filters,"Ürün durumu",activeFilter);FilterField(filters,"Ürün markası",brandFilter);FilterField(filters,"Ürün kaynağı",sourceFilter);FilterField(filters,"Stok durumu",stockFilter);groups.Children.Add(ControlGroup("Ürün filtreleri",filters,245));
        var other=new StackPanel();statusFilter.MinWidth=125;FilterField(other,"Yayın durumu",statusFilter);FilterField(other,"Para birimi",currencyFilter);other.Children.Add(T("Kategori seçimi alt kategorileri de kapsar. Filtreler birlikte uygulanır.",true));groups.Children.Add(ControlGroup("Diğer filtreler",other,270));
        var scroll=Scroll(groups);scroll.MaxHeight=285;detailedFilters.Children.Add(scroll);var buttons=new WrapPanel();var apply=Primary(B("Filtreleri uygula",()=>{productOffset=0;RefreshProducts();}));apply.Name="TrendyolApplyFilters";buttons.Children.Add(apply);var reset=B("Filtreleri temizle",ResetControlFilters);reset.Name="TrendyolResetFilters";buttons.Children.Add(reset);detailedFilters.Children.Add(buttons);
    }
    void BuildBulkActions()
    {
        var operations=new StackPanel();foreach(var operation in new[]{TrendyolOperation.PriceAndStock,TrendyolOperation.Stock,TrendyolOperation.Price,TrendyolOperation.Content,TrendyolOperation.UpdateUnapproved})operations.Children.Add(B(ModeLabel(operation)+" · önizle",()=>PreviewOperation(operation)));bulkActions.Children.Add(ControlGroup("Toplu işlemler · seçili ürünler",operations,260));
        var other=new StackPanel();other.Children.Add(B("Trendyol eşleştir",MatchProducts));other.Children.Add(B("Yeni ürün oluştur · önizle",()=>PreviewOperation(TrendyolOperation.Create)));other.Children.Add(B("Ürün bilgilerini düzenle",OpenProductEditor));other.Children.Add(B("Mesajlar / işlem geçmişi",()=>ShowOperationHistory(null)));bulkActions.Children.Add(ControlGroup("Diğer işlemler",other,230));
        var results=new StackPanel();results.Children.Add(B("Ürün ekleme sonuçları",()=>ShowOperationHistory(TrendyolOperation.Create)));results.Children.Add(B("Ürün güncelleme sonuçları",()=>ShowOperationHistory(TrendyolOperation.Content)));results.Children.Add(B("Adet / fiyat sonuçları",()=>ShowOperationHistory(TrendyolOperation.PriceAndStock)));results.Children.Add(T("İşlemi seçip API sonucunu sorgulayabilirsiniz.",true));bulkActions.Children.Add(ControlGroup("İşlem durumu sorgulama",results,230));
        var templatesForm=new StackPanel();bulkDelivery.MinWidth=120;templatesForm.Children.Add(bulkDelivery);templatesForm.Children.Add(B("Seçili ürünlere şablonu ata",()=>AssignTemplate(bulkDelivery.SelectedItem as TrendyolDeliveryTemplate)));templatesForm.Children.Add(B("Teslimat süresini önizle",()=>PreviewOperation(TrendyolOperation.Delivery)));templatesForm.Children.Add(B("Kargo / adresleri önizle",()=>PreviewOperation(TrendyolOperation.ShippingDetails)));bulkActions.Children.Add(ControlGroup("Toplu teslimat şablonu",templatesForm,260));
        // Keep all four action groups on one row at the standard desktop width.
        foreach(var group in bulkActions.Children.OfType<GroupBox>()){
            group.Width=group.Header.ToString()!.StartsWith("Toplu teslimat")?240:215;group.Padding=new(4);group.Margin=new(3);
            if(group.Content is Panel content)foreach(var button in content.Children.OfType<Button>()){button.MinHeight=24;button.Padding=new(5,2,5,2);button.Margin=new(2);}
        }
    }
    void PreviewOperation(TrendyolOperation operation){mode.SelectedItem=mode.Items.Cast<Mode>().Single(m=>m.Value==operation);BuildPreview();}
    void ShowOperationHistory(TrendyolOperation? operation)
    {
        ReloadHistory();if(operation.HasValue)history.ItemsSource=history.Items.Cast<TrendyolReceipt>().Where(r=>operation==TrendyolOperation.Create?r.Operation=="Create":operation==TrendyolOperation.PriceAndStock?r.Operation is "Price" or "Stock" or "PriceAndStock":r.Operation is "Content" or "UpdateUnapproved" or "ShippingDetails" or "Delivery").ToList();tabs.SelectedIndex=3;
    }
    void ConfigureControlGrid()
    {
        products.FontSize=11.5;products.RowHeight=27;products.ColumnHeaderHeight=29;products.HeadersVisibility=DataGridHeadersVisibility.Column;products.GridLinesVisibility=DataGridGridLinesVisibility.All;products.HorizontalGridLinesBrush=Line;products.VerticalGridLinesBrush=Line;products.BorderBrush=Line;products.Margin=new(0,3,0,0);products.AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(242,245,247));products.Background=Brushes.White;products.EnableColumnVirtualization=true;
        var cellText=new Style(typeof(TextBlock));cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(3,0,3,0)));cellText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));cellText.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Text"){RelativeSource=new RelativeSource(RelativeSourceMode.Self)}));foreach(var column in products.Columns.OfType<DataGridTextColumn>())column.ElementStyle=cellText;
        products.FrozenColumnCount=2;
        var statusStyle=new Style(typeof(TextBlock),cellText);statusStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,new Binding("StatusColor")));statusStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty,FontWeights.SemiBold));statusStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("StatusDetail")));products.Columns.OfType<DataGridTextColumn>().Single(c=>(c.Binding as Binding)?.Path.Path=="Listed").ElementStyle=statusStyle;
        var rowStyle=new Style(typeof(DataGridRow));foreach(var key in new[]{"rejected","blacklisted","locked","documentRequired"}){var issue=new DataTrigger{Binding=new Binding("StatusKey"),Value=key};issue.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(255,222,176))));rowStyle.Triggers.Add(issue);}var selected=new Trigger{Property=DataGridRow.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(199,225,235))));selected.Setters.Add(new Setter(Control.ForegroundProperty,Ink));rowStyle.Triggers.Add(selected);products.RowStyle=rowStyle;
        var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(4,2,4,2)));headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(232,239,242))));headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,Ink));products.ColumnHeaderStyle=headerStyle;
    }
    void RefreshControlFilterOptions()
    {
        var items=catalog.Products();updatingFilterOptions=true;try{
            void Options(ComboBox box,IEnumerable<string> values){var selected=box.SelectedItem as string??"Tümü";var list=new[]{"Tümü"}.Concat(values.Where(v=>v.Length>0).Distinct().OrderBy(v=>v,StringComparer.CurrentCultureIgnoreCase)).ToList();box.ItemsSource=list;box.SelectedItem=list.Contains(selected)?selected:"Tümü";}
            Options(brandFilter,items.Select(p=>p.Brand));Options(sourceFilter,items.Select(p=>p.SourceKind));Options(currencyFilter,items.Select(p=>p.Currency));categoryFilterTree.Items.Clear();var nodes=new Dictionary<string,TreeViewItem>();
            foreach(var path in items.Select(p=>p.Category).Where(p=>p.Length>0).Distinct().OrderBy(p=>p)){
                ItemsControl parent=categoryFilterTree;var prefix="";foreach(var part in path.Split('>').Select(p=>p.Trim()).Where(p=>p.Length>0)){prefix=prefix.Length==0?part:prefix+" > "+part;var key=TrendyolMatching.Normalize(prefix);if(!nodes.TryGetValue(key,out var node)){node=new TreeViewItem{Header=part,Tag=prefix,ToolTip=prefix};nodes[key]=node;parent.Items.Add(node);}if(TaxonomyStore.SameCategory(prefix,selectedFilterCategory))node.IsSelected=true;parent=node;}
            }
            bulkDelivery.ItemsSource=state.Templates;bulkDelivery.SelectedIndex=state.Templates.Count>0?0:-1;
        }finally{updatingFilterOptions=false;}
    }
    void ResetControlFilters()
    {
        selectedFilterCategory="";productSearch.Clear();foreach(var combo in new[]{brandFilter,sourceFilter,currencyFilter,activeFilter,stockFilter,linkedFilter,mappingFilter,descriptionFilter,imageFilter})combo.SelectedIndex=0;statusFilter.SelectedIndex=0;RefreshControlFilterOptions();productOffset=0;RefreshProducts();
    }
    bool MatchesDetailedFilters(TrendyolProductRow row)
    {
        var p=row.Product;
        bool Presence(ComboBox box,string value)=>box.SelectedIndex==0||(box.SelectedIndex==1?!string.IsNullOrWhiteSpace(value):string.IsNullOrWhiteSpace(value));
        bool Exact(ComboBox box,string value)=>box.SelectedIndex<=0||(string?)box.SelectedItem==value;
        var categoryMatches=selectedFilterCategory.Length==0||TaxonomyStore.SameCategory(p.Category,selectedFilterCategory)||TrendyolMatching.Normalize(string.Join(" > ",p.Category.Split('>').Select(x=>x.Trim()))).StartsWith(TrendyolMatching.Normalize(selectedFilterCategory)+" > ",StringComparison.Ordinal);
        return categoryMatches&&Exact(brandFilter,p.Brand)&&Exact(sourceFilter,p.SourceKind)&&Exact(currencyFilter,p.Currency)&&Presence(descriptionFilter,p.Description)&&Presence(imageFilter,p.ImageUrls)
            &&(activeFilter.SelectedIndex==0||(activeFilter.SelectedIndex==1?p.Active:!p.Active))&&(stockFilter.SelectedIndex==0||(stockFilter.SelectedIndex==1?p.Stock>0:p.Stock<=0))
            &&(linkedFilter.SelectedIndex==0||(linkedFilter.SelectedIndex==1?row.StatusKey is not ("unmatched" or "missing"):row.StatusKey is "unmatched" or "missing"))
            &&(mappingFilter.SelectedIndex==0||(mappingFilter.SelectedIndex==1?!row.CategoryId.HasValue:mappingFilter.SelectedIndex==2?row.MappingSummary.Contains("Marka eksik"):!row.MappingIncomplete));
    }
}
