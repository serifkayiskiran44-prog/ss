using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

namespace TrMarketplaceHubDesktop;

public sealed partial class EtsyWorkspacePanel : UserControl, IDisposable
{
    readonly string? directory;
    readonly CatalogStore catalog;
    readonly EtsyWorkspaceStore store;
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    readonly CancellationTokenSource lifetime = new();
    readonly TabControl sections = new() { Name = "EtsySections" };
    readonly TabControl settings = new() { Name = "EtsySettingsSections" };
    readonly DataGrid products = Table("EtsyProducts"), history = Table("EtsyHistory");
    readonly TextBox search = Input();
    readonly TextBlock summary = Note("Bağlantı ayarlarını açıp mağazanızı doğrulayın."), count = Note("");
    readonly ComboBox stateFilter = Choices("Tüm ürünler", "Eşleşenler", "Eşleşmeyenler", "Stokta olanlar");
    readonly ComboBox templateChoice = Select();
    readonly Button send = new() { Name = "EtsySend", Content = "Önizlemeyi aç", IsEnabled = false };
    EtsyCredentials? credentials;
    EtsyWorkspaceState state = new();
    EtsyOperationPlan? plan;
    int page;
    bool busy;
    bool disposed;
    public event Action<EtsyCredentials>? CredentialsChanged;

    public EtsyWorkspacePanel(string? directory = null)
    {
        this.directory = directory; catalog = new(directory); store = new(directory);
        BuildStyle();
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new DockPanel { Margin = new Thickness(0,0,0,10) };
        var open = ActionButton("Mağaza ayarları", ShowSettings); open.Name = "EtsyOpenSettings";
        DockPanel.SetDock(open,Dock.Right); top.Children.Add(open); top.Children.Add(summary);
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        sections.Items.Add(new TabItem { Header = "Etsy kontrol", Content = BuildControl() });
        sections.Items.Add(new TabItem { Header = "Ayarlar", Content = BuildSettings() });
        sections.Items.Add(new TabItem { Header = "İşlem geçmişi", Content = BuildHistory() });
        root.Children.Add(sections); Content = root;
        try { credentials = CredentialStore.Load(directory); if(credentials is not null) { FillCredentials(credentials); LoadState(); } }
        catch(Exception ex) { summary.Text = Safe(ex); }
        RefreshProducts();
        Loaded += (_,_) => { var window=Window.GetWindow(this); if(window is not null) window.Closed += (_,_)=>Dispose(); };
    }
    public void Dispose() { if(disposed)return;disposed=true;lifetime.Cancel();http.Dispose();lifetime.Dispose(); }
    public void ShowSettings() { sections.SelectedIndex=1; settings.SelectedIndex=0; }
    static string Safe(Exception error) => MarketplaceConnectionStore.Redact(error.Message);
    async Task Run(Func<Task> action)
    {
        if(busy)return; busy=true; IsEnabled=false;
        try { await action(); }
        catch(OperationCanceledException) { summary.Text="İşlem iptal edildi."; }
        catch(Exception ex) { summary.Text=Safe(ex); MessageBox.Show(Window.GetWindow(this), Safe(ex),"Etsy",MessageBoxButton.OK,MessageBoxImage.Warning); }
        finally { busy=false; IsEnabled=true; }
    }
    void Local(Action action) { try { action(); } catch(Exception ex) { summary.Text=Safe(ex);MessageBox.Show(Window.GetWindow(this),Safe(ex),"Etsy",MessageBoxButton.OK,MessageBoxImage.Warning); } }
    void LoadState()
    {
        if(credentials is null || string.IsNullOrWhiteSpace(credentials.ShopId))return;
        state=store.Load(credentials.ShopId); ReloadChoices();
        summary.Text=$"{(state.ShopName.Length>0?state.ShopName:"Etsy mağazası")}  /  {state.ShopId}  ·  {state.Currency}  ·  {state.Listings.Count:N0} ilan";
        history.ItemsSource=store.Receipts(state.ShopId); RefreshProducts();
    }
    void SaveState() { if(string.IsNullOrEmpty(state.ShopId))throw new InvalidOperationException("Önce Etsy mağazasını doğrulayın.");store.Save(state);state=store.Load(state.ShopId);ClearPreview();ReloadChoices();RefreshProducts(); }
    void ClearPreview() { plan=null;send.IsEnabled=false; }
    IReadOnlyList<string> SelectedIds() => products.SelectedItems.Cast<ProductRow>().Select(r=>r.Id).ToArray();
    IReadOnlyList<string> RequireSelection() { var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("Önce ürün listesinden ürün seçin.");return ids; }
    void RefreshProducts()
    {
        var selected=SelectedIds().ToHashSet();
        var rows=catalog.Products(search.Text.Trim()).Select(p=>
        {
            var profile=state.Profiles.FirstOrDefault(x=>x.ProductId==p.Id);
            var id=profile?.ListingId;
            var remote=id.HasValue?state.Listings.FirstOrDefault(x=>x.ListingId==id):null;
            return new ProductRow(p.Id,p.Sku,p.Name,p.Barcode,p.Stock,p.Price,p.Currency,id,remote?.Quantity,remote?.Price,remote?.Currency??state.Currency,
                remote is null?(id.HasValue?"Doğrulanacak":"Eşleşmedi"):StateLabel(remote.State),p.Category,p.Brand,
                storeMessage(p.Id),profile?.TemplateId??"");
        }).Where(p=>stateFilter.SelectedIndex switch {1=>p.ListingId.HasValue,2=>!p.ListingId.HasValue,3=>p.Stock>0,_=>true}).ToList();
        page=Math.Clamp(page,0,Math.Max(0,(rows.Count-1)/100)); products.ItemsSource=rows.Skip(page*100).Take(100).ToList();
        foreach(var item in products.Items.Cast<ProductRow>().Where(x=>selected.Contains(x.Id)))products.SelectedItems.Add(item);
        count.Text=$"{rows.Count:N0} ürün · Sayfa {page+1} / {Math.Max(1,(rows.Count+99)/100)} · {products.SelectedItems.Count} seçili";
        string storeMessage(string id)=>history.ItemsSource is IReadOnlyList<EtsyOperationReceipt> receipts?receipts.FirstOrDefault(r=>r.ProductId==id)?.Detail??"":"";
    }
    static string StateLabel(string value)=>value switch {"active"=>"Yayında","draft"=>"Taslak","inactive"=>"Pasif","sold_out"=>"Tükendi","expired"=>"Süresi doldu",_=>value};
    sealed record ProductRow(string Id,string Sku,string Title,string Barcode,int Stock,decimal Price,string Currency,long? ListingId,int? EtsyStock,decimal? EtsyPrice,string EtsyCurrency,string State,string Category,string Brand,string Detail,string TemplateId);
    sealed record Option(string Id,string Name) { public override string ToString()=>Name; }
    static void SetOptions(ComboBox combo,IEnumerable<Option> values,string? selected=null)
    { var old=selected??(combo.SelectedItem as Option)?.Id;combo.ItemsSource=values.ToList();combo.SelectedValuePath=nameof(Option.Id);combo.DisplayMemberPath=nameof(Option.Name);combo.SelectedValue=old; }
    static long? Id(ComboBox combo)=>long.TryParse((combo.SelectedItem as Option)?.Id,out var id)&&id>0?id:null;
    static string Selected(ComboBox combo)=>(combo.SelectedItem as Option)?.Id??"";
    static ComboBox Select()=>new(){MinWidth=160,IsTextSearchEnabled=true,IsEditable=false};
    static TextBox Input(string value="")=>new(){Text=value,MinWidth=150};
    static TextBlock Note(string value)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(92,116,131)),Margin=new Thickness(3,4,3,5)};
    static ComboBox Choices(params string[] options)=>new(){ItemsSource=options,SelectedIndex=0,MinWidth=170};
    static DataGrid Table(string name)=>new(){Name=name,AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,EnableRowVirtualization=true,EnableColumnVirtualization=true,RowHeaderWidth=0,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeight=30,MinHeight=130};
    static void Column(DataGrid grid,string title,string binding,double width=130)=>grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(binding),Width=new DataGridLength(width)});
    Button ActionButton(string title,Action action) {var button=new Button{Content=title};button.Click+=(_,_)=>Local(action);return button;}
    Button AsyncButton(string title,Func<Task> action) {var button=new Button{Content=title};button.Click+=async(_,_)=>await Run(action);return button;}
    static WrapPanel Bar(params UIElement[] elements) {var bar=new WrapPanel();foreach(var element in elements)bar.Children.Add(element);return bar;}
    static FrameworkElement Field(string label,FrameworkElement control) {var stack=new StackPanel{Margin=new Thickness(4,3,10,6)};stack.Children.Add(Note(label));stack.Children.Add(control);return stack;}
    static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    static Border Section(string title,UIElement content)
    { var dock=new DockPanel();var heading=new TextBlock{Text=title,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);dock.Children.Add(content);return new Border{BorderBrush=new SolidColorBrush(Color.FromRgb(217,226,230)),BorderThickness=new Thickness(1),Padding=new Thickness(12),Margin=new Thickness(4),Child=dock}; }
    void BuildStyle()
    {
        var border=new SolidColorBrush(Color.FromRgb(210,223,230));var ink=new SolidColorBrush(Color.FromRgb(32,55,66));
        foreach(var type in new[]{typeof(Button),typeof(TextBox),typeof(PasswordBox),typeof(ComboBox)})
        {var style=new Style(type);style.Setters.Add(new Setter(Control.MinHeightProperty,30d));style.Setters.Add(new Setter(Control.MarginProperty,new Thickness(3)));style.Setters.Add(new Setter(Control.BorderBrushProperty,border));style.Setters.Add(new Setter(Control.ForegroundProperty,ink));style.Setters.Add(new Setter(Control.FontSizeProperty,12d));if(type==typeof(Button)){style.Setters.Add(new Setter(Control.HeightProperty,32d));style.Setters.Add(new Setter(Control.VerticalAlignmentProperty,VerticalAlignment.Bottom));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,4,10,4)));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.White));}Resources[type]=style;}
    }
}
