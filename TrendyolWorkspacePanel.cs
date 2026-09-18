using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed partial class TrendyolWorkspacePanel : UserControl
{
    readonly string? workspaceDirectory;
    readonly CatalogStore catalog;
    readonly TaxonomyStore taxonomy;
    readonly TrendyolWorkspaceStore store;
    readonly ProductChannelCreationPreviewInbox creationInbox;
    readonly TrendyolSettingsStore legacyCredentials;
    readonly MarketplaceCredentialVault? credentialVault;
    readonly MarketplaceConnection? scopedConnection;
    readonly TaxonomyContentStore contentTemplates;
    readonly TabControl tabs=new(){Name="TrendyolSections",SelectedIndex=0};
    readonly TabControl settingsTabs=new(){Name="TrendyolSettingsSections"};
    readonly TextBlock accountBadge=T("");
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(8),Foreground=Brushes.DarkSlateGray};
    readonly TextBlock accountStatus=new(){TextWrapping=TextWrapping.Wrap,Margin=new(8)};
    readonly TextBox seller=Box();
    readonly PasswordBox apiKey=new(){MinWidth=250,Margin=new(3)};
    readonly PasswordBox apiSecret=new(){MinWidth=250,Margin=new(3)};
    readonly Button send=new(){Name="TrendyolSend",Content="Önizlemeyi onayla ve gönder",IsEnabled=false,Margin=new(4),Padding=new(12,7,12,7)};
    readonly Button creationHandoffButton=new(){Name="TrendyolCreationHandoffButton",Content="Merkezden gelen yeni ilanlar",IsEnabled=false,Margin=new(3),Padding=new(9,5,9,5)};
    TrendyolWorkspaceState state=new();
    TrendyolPlan? plan;
    MarketplaceShopProductsModel? accountSpecialistModel;
    string accountSpecialistPlanId="";
    CancellationTokenSource? cancellation;
    bool loaded;
    public string? ConnectionId => scopedConnection?.Id;
    public string AccountShopId => scopedConnection?.ShopId ?? LoadAccount()?.SupplierId ?? "";
    public MarketplaceShopSpecialistPreview? AccountSpecialistPreview { get; private set; }
    public TrendyolOperation? AccountSpecialistOperation => plan?.Operation;
    public IReadOnlyList<string> AccountSpecialistPlanProductIds => plan?.Rows.Select(row => row.ProductId).ToArray() ?? Array.Empty<string>();

    public TrendyolWorkspacePanel(string? directory=null) : this(directory, null, false) { }

    public TrendyolWorkspacePanel(string connectionId, string? directory) : this(directory, connectionId, true) { }

    TrendyolWorkspacePanel(string? directory, string? connectionId, bool accountScoped)
    {
        workspaceDirectory = directory;
        if (accountScoped)
        {
            scopedConnection = new MarketplaceConnectionStore(directory).Get(connectionId!)
                ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
            if (!scopedConnection.Enabled || scopedConnection.Channel != "trendyol")
                throw new InvalidOperationException("Trendyol çalışma alanı için etkin ve doğru kanal hesabı gerekli.");
            credentialVault = new MarketplaceCredentialVault(directory);
        }
        catalog=new(directory);taxonomy=new(directory);store=new(directory);creationInbox=new(directory);contentTemplates=new(directory);legacyCredentials=new(directory is null?null:Path.Combine(directory,"trendyol.bin"));
        creationHandoffButton.Click+=(_,_)=>{try{AcceptCreationHandoff();}catch(Exception ex){status.Text=Safe(ex);}};
        ApplyWorkspaceStyle();
        var root=new DockPanel{Margin=new(10)};var footer=new DockPanel();var cancel=B("İsteği iptal et",()=>cancellation?.Cancel());DockPanel.SetDock(cancel,Dock.Right);footer.Children.Add(cancel);footer.Children.Add(status);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var header=new DockPanel{Margin=new(0,0,0,8)};var settingsButton=B("Mağaza ayarları",ShowSettings);settingsButton.Name="TrendyolOpenSettings";DockPanel.SetDock(settingsButton,Dock.Right);header.Children.Add(settingsButton);accountBadge.FontWeight=FontWeights.SemiBold;header.Children.Add(accountBadge);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);root.Children.Add(tabs);Content=root;
        settingsTabs.Items.Add(new TabItem{Header="Bağlantı ve aktarım",Content=BuildConnection()});settingsTabs.Items.Add(new TabItem{Header="Kategori ve marka",Content=BuildMappings()});settingsTabs.Items.Add(new TabItem{Header="Teslimat şablonları",Content=BuildDelivery()});settingsTabs.Items.Add(new TabItem{Header="Marka denetim bilgileri",Content=BuildBrandSafety()});
        if(scopedConnection is not null)settingsTabs.Items.Add(new TabItem{Header="Hesap kuralları",Content=BuildAccountShopSettings()});
        AddTab("Trendyol kontrol",BuildAccountShopProducts(BuildProducts()));AddTab("Ayarlar",settingsTabs);AddTab("Rekabet analizi",BuildCompetition());AddTab("İşlem geçmişi",BuildHistory());
        tabs.SelectionChanged+=(_,e)=>{if(e.Source==tabs&&loaded&&editorDirty&&tabs.SelectedIndex!=0&&!DiscardEditorChanges())tabs.SelectedIndex=0;};
        send.Click+=async(_,_)=>await Run(async()=>{
            var current=plan??throw new InvalidOperationException("Önce önizleyin.");
            if(MessageBox.Show(Window.GetWindow(this),$"Satıcı: {current.SellerId}\nİşlem: {ModeLabel(current.Operation)}\n{current.Rows.Count(r=>r.ItemJson!=null)} ürün Trendyol'a gönderilecek.\nEkrandaki önizlemeyi onaylıyor musunuz?","Trendyol gönderim onayı",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var account=Account();using var client=new TrendyolApiClient(account);TrendyolReceipt receipt;
            try{receipt=await store.SendAsync(current.Id,account,true,client,Token,beforeClaim:accountSpecialistPlanId==current.Id?()=>accountSpecialistModel!.ValidateSpecialistPlan(current.Id):null);}
            finally{InvalidatePreview();}
            ReloadHistory();status.Text=receipt.Detail;tabs.SelectedIndex=3;
        });
        try{var saved=LoadAccount();seller.Text=scopedConnection?.ShopId??saved?.SupplierId??"";seller.IsReadOnly=scopedConnection is not null;Reload();}catch(Exception ex){status.Text=Safe(ex);}
        loaded=true;Unloaded+=(_,_)=>cancellation?.Cancel();
    }
    CancellationToken Token=>cancellation?.Token??CancellationToken.None;
    TrendyolSettings Account()=>LoadAccount()??throw new InvalidOperationException("Önce Ayarlar → Bağlantı ve aktarım bölümünde satıcı ID ve API bilgilerini kaydedin.");
    public void ShowSettings(){tabs.SelectedIndex=1;settingsTabs.SelectedIndex=0;}
    void AddTab(string name,UIElement content)=>tabs.Items.Add(new TabItem{Header=name,Content=content});
    void Reload()
    {
        var saved=LoadAccount();var accountShop=scopedConnection?.ShopId??saved?.SupplierId;state=string.IsNullOrWhiteSpace(accountShop)?new():store.Load(accountShop);InvalidatePreview();competition.ItemsSource=null;competitionAccount="";
        accountStatus.Text=saved is null?"API bilgisi yok. Açılışta bağlantı kurulmaz.":$"Satıcı: {saved.SupplierId} · Ürün API V2 · Anahtarlar şifreli kayıtlı";
        accountBadge.Text=scopedConnection is not null?$"{scopedConnection.DisplayName}  /  {scopedConnection.ShopId}     •     {scopedConnection.Status}":saved is null?"Trendyol mağazası · Bağlantı ayarları gerekli":$"Trendyol mağazası  /  {saved.SupplierId}     •     Türkiye · TRY";
        RefreshMappings();RefreshControlFilterOptions();RefreshProducts();RefreshTemplates();RefreshBrandSafety();ReloadHistory();RefreshCreationHandoff();
        status.Text=$"Kategori: {state.Categories.Count} · Marka: {state.Brands.Count} · Mağaza ürünü: {state.Products.Count} · Sözlük: {Time(state.DictionaryUpdatedUtc)} · Ürünler: {Time(state.ProductsUpdatedUtc)}";
    }
    void RefreshCreationHandoff()
    {
        if(scopedConnection is null){creationHandoffButton.IsEnabled=false;return;}
        var pending=creationInbox.Pending(scopedConnection.Id);
        creationHandoffButton.IsEnabled=pending.Count>0;
        creationHandoffButton.Content=pending.Count==0?"Merkezden gelen yeni ilanlar":$"Merkezden gelen yeni ilanlar ({pending.Sum(item=>item.ProductIds.Count)})";
    }
    void AcceptCreationHandoff()
    {
        var connection=CurrentScopedConnection();
        var request=creationInbox.Pending(connection.Id).FirstOrDefault()??throw new InvalidOperationException("Bu Trendyol hesabı için bekleyen yeni ilan önizlemesi yok.");
        var next=store.Preview(Account(),request.ProductIds,TrendyolOperation.Create);
        creationInbox.Recognize(request.Id,connection.Id);
        PresentPreview(next);
        RefreshCreationHandoff();
        status.Text=$"{request.ProductIds.Count} ürün bu hesapta yeni ürün oluşturma önizlemesine alındı. Otomatik gönderim yapılmadı.";
    }
    void Persist(){try{store.Save(state);}catch{state=store.Load(Account().SupplierId);throw;}Reload();}
    void InvalidatePreview(){ClearAccountSpecialistAssociation();plan=null;send.IsEnabled=false;preview.ItemsSource=null;payload.Text="";if(productPreviewView.Visibility==Visibility.Visible)ShowProductView("list");}
    void ClearAccountSpecialistAssociation(){if(accountSpecialistPlanId.Length>0)accountSpecialistModel?.ClearSpecialistPlanAssociation(accountSpecialistPlanId);accountSpecialistPlanId="";accountSpecialistModel=null;AccountSpecialistPreview=null;}
    async Task Run(Func<Task> action)
    {
        if(cancellation!=null)return;cancellation=new();tabs.IsEnabled=false;status.Text="İşlem sürüyor…";
        try{await action();}catch(OperationCanceledException){status.Text="İstek iptal edildi; tamamlanmayan okuma önbelleğe yazılmadı.";}catch(Exception ex){status.Text=Safe(ex);}finally{tabs.IsEnabled=true;cancellation.Dispose();cancellation=null;}
    }
    static string Safe(Exception ex)=>MarketplaceConnectionStore.Redact(ex.Message);
    static string Time(DateTime? time)=>time?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"henüz alınmadı";
    static TextBox Box()=>new(){MinWidth=180,Margin=new(3),Padding=new(4)};
    static ComboBox Combo(string display="Name")=>new(){MinWidth=180,Margin=new(3),DisplayMemberPath=display,IsTextSearchEnabled=true};
    static TextBlock T(string text,bool muted=false)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(5),Foreground=muted?Brushes.SlateGray:Brushes.DarkSlateGray};
    Button B(string text,Action action){var button=new Button{Content=text,Margin=new(3),Padding=new(9,5,9,5)};button.Click+=(_,_)=>{try{action();}catch(Exception ex){status.Text=Safe(ex);}};return button;}
    Button A(string text,Func<Task> action){var button=B(text,()=>{});button.Click+=async(_,_)=>await Run(action);return button;}
    static void Field(Panel panel,string label,UIElement input){panel.Children.Add(T(label));panel.Children.Add(input);}
    static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    static DataGrid Grid(string name,params (string Header,string Path,int Width)[] columns)
    {
        var grid=new DataGrid{Name=name,AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,CanUserAddRows=false,Margin=new(4),MinHeight=110,EnableRowVirtualization=true};
        foreach(var (header,path,width) in columns)grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(path),Width=width});return grid;
    }
    FrameworkElement BuildConnection()
    {
        var form=new StackPanel();
        form.Children.Add(T("Trendyol satıcı panelinizdeki entegrasyon bilgileri",true));Field(form,"Satıcı ID (Cari ID)",seller);Field(form,"API anahtarı",apiKey);Field(form,"API secret",apiSecret);
        form.Children.Add(T("Kayıtlı anahtarı değiştirmeyecekseniz boş bırakın.",true));var actions=new WrapPanel();
        actions.Children.Add(Primary(B("Ayarları kaydet",()=>{
            var previous=LoadAccount();var same=previous?.SupplierId==seller.Text.Trim();
            var account=new TrendyolSettings(seller.Text.Trim(),apiKey.Password.Length>0?apiKey.Password:same?previous!.ApiKey:"",apiSecret.Password.Length>0?apiSecret.Password:same?previous!.ApiSecret:"",seller.Text.Trim()+" - Self Integration");
            SaveAccount(account);apiKey.Clear();apiSecret.Clear();Reload();status.Text="Bağlantı bilgileri bu Windows kullanıcısı için şifrelenerek kaydedildi.";
        })));
        actions.Children.Add(A("Bağlantıyı kontrol et",async()=>{var account=Account();using var client=new TrendyolApiClient(account);var addresses=await client.GetAddressesAsync(Token);EnsureAccount(account);accountStatus.Text=$"Bağlantı başarılı · Satıcı {account.SupplierId} · {addresses.Count} adres";status.Text="Mağaza bağlantısı doğrulandı.";}));form.Children.Add(actions);form.Children.Add(accountStatus);
        var setup=new StackPanel();setup.Children.Add(T("Kategori, marka ve mağaza ürünlerini alın; bulunan karşılıkları inceleyip kaydedin.",true));setup.Children.Add(A("Kategori ve marka listesini yenile",RefreshDictionaries));setup.Children.Add(B("Kategori / marka eşleştirmelerini aç",()=>{tabs.SelectedIndex=1;settingsTabs.SelectedIndex=1;}));setup.Children.Add(A("Trendyol ürünlerini yenile",RefreshStoreProducts));setup.Children.Add(B("Teslimat ve kargo şablonlarını aç",()=>{tabs.SelectedIndex=1;settingsTabs.SelectedIndex=2;}));
        var transfer=new StackPanel();transfer.Children.Add(T("Yeni ürün ekleme, yalnız fiyat, yalnız stok ve diğer işlemler ürün listesindeki İşlem alanından seçilir."));transfer.Children.Add(T("Fiyat: KDV dahil TRY\nÜrün eşleştirme: barkod\nStok kodu ayrı korunur\nGönderim: seçili ürünler → önizleme → onay",true));transfer.Children.Add(Primary(B("Trendyol kontrolü aç",()=>{tabs.SelectedIndex=0;ShowProductView("list");})));
        var right=new StackPanel();right.Children.Add(Section("Listeler ve eşleştirme",setup));right.Children.Add(Section("Ürün aktarımı",transfer));
        return Scroll(Columns(Section("API / kullanıcı bilgileri",form),right));
    }
    void EnsureAccount(TrendyolSettings captured){Token.ThrowIfCancellationRequested();if(TrendyolWorkspaceStore.AccountFingerprint(Account())!=TrendyolWorkspaceStore.AccountFingerprint(captured))throw new InvalidOperationException("İstek sırasında mağaza bilgileri değişti; sonuç kaydedilmedi.");}

    TrendyolSettings? LoadAccount()
    {
        if (scopedConnection is null) return legacyCredentials.Load();
        var current = CurrentScopedConnection();
        var value = credentialVault!.Load<TrendyolSettings>(current.Id, current.Channel, current.ShopId);
        if (value is not null && value.SupplierId != current.ShopId)
            throw new InvalidOperationException("WRONG_ACCOUNT: Trendyol şifreli hesabı seçili mağazayla eşleşmiyor.");
        return value;
    }

    void SaveAccount(TrendyolSettings value)
    {
        if (scopedConnection is null) { legacyCredentials.Save(value); return; }
        var current = CurrentScopedConnection();
        if (value.SupplierId != current.ShopId)
            throw new InvalidOperationException("WRONG_ACCOUNT: Trendyol satıcı kimliği seçili mağazayla eşleşmiyor.");
        credentialVault!.Save(current.Id, current.Channel, current.ShopId, value);
    }

    MarketplaceConnection CurrentScopedConnection()
    {
        var current = new MarketplaceConnectionStore(workspaceDirectory).Get(scopedConnection!.Id)
            ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!current.Enabled || current.Channel != "trendyol" || current.ShopId != scopedConnection.ShopId)
            throw new InvalidOperationException("WRONG_ACCOUNT: Mağaza bağlantısı devre dışı veya değiştirilmiş.");
        return current;
    }

}
