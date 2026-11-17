using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Etsy;

namespace TrMarketplaceHubDesktop;
public sealed partial class EtsyWorkspacePanel
{
    readonly PasswordBox key=new(), secret=new(), token=new(), refreshToken=new();
    readonly TextBox shop=Input(), redirect=Input("https://localhost:5099/etsy/callback"), callback=Input();
    readonly TextBlock connectionStatus=Note("Kayıtlı erişim bilgileri Windows kullanıcı hesabına bağlı olarak şifrelenir.");
    readonly ComboBox localCategory=Select(), categoryTarget=Select(), templateList=Select();
    readonly TextBox categorySearch=Input();
    readonly DataGrid mappings=Table("EtsyCategoryMappings");
    OAuthAttempt? oauthAttempt;
    UIElement BuildSettings()
    {
        var form=new StackPanel();
        form.Children.Add(Note("1. Bağlantıyı kaydedin ve doğrulayın.  2. Mağaza kategorileri ve şablonlarını alın.  3. Ürünleri hazırlayıp önizleyin."));
        var credentialsForm=new StackPanel();
        key.Width=270;secret.Width=270;token.Width=270;refreshToken.Width=270;shop.Width=180;redirect.Width=490;callback.MinWidth=550;
        credentialsForm.Children.Add(Bar(Field("API Key (Keystring)",key),Field("API Secret (Shared Secret)",secret),Field("Mağaza ID",shop)));
        credentialsForm.Children.Add(Bar(Field("Erişim tokenı",token),Field("Yenileme tokenı",refreshToken)));
        credentialsForm.Children.Add(Bar(ActionButton("Bilgileri güvenli kaydet",SaveConnection),AsyncButton("Bağlantıyı doğrula",TestConnection),AsyncButton("Kategoriler / mağaza şablonlarını al",PullMetadata)));
        credentialsForm.Children.Add(connectionStatus);form.Children.Add(Section("Etsy mağaza bağlantısı",credentialsForm));
        var oauth=new StackPanel();oauth.Children.Add(Note("Etsy uygulamasında kayıtlı dönüş adresini kullanın. Etsy'de izin verdikten sonra açılan adresi buraya yapıştırın."));oauth.Children.Add(Field("Kayıtlı HTTPS dönüş adresi",redirect));
        oauth.Children.Add(ActionButton("Etsy hesabını bağla",()=>{var c=ReadCredentials();oauthAttempt=new EtsyOAuth(http).Begin(c.Key,c.RedirectUri);Process.Start(new ProcessStartInfo(oauthAttempt.AuthorizeUrl){UseShellExecute=true});connectionStatus.Text="Etsy yetkilendirmesi açıldı. Dönüş adresini aşağıya yapıştırın.";}));
        oauth.Children.Add(Field("Yetkilendirme dönüş adresi",callback));oauth.Children.Add(AsyncButton("Bağlantıyı tamamla",async()=>{var c=ReadCredentials();if(oauthAttempt is null)throw new InvalidOperationException("Önce Etsy hesabını bağla düğmesiyle başlatın.");var attempt=oauthAttempt;oauthAttempt=null;var connected=await new EtsyOAuth(http).ExchangeAsync(c,attempt,callback.Text.Trim(),lifetime.Token);await new EtsyMetadataClient(http).GetShopAsync(connected,lifetime.Token);PersistCredentials(connected);callback.Clear();await TestConnection();}));form.Children.Add(Section("Tarayıcı ile yetkilendirme",oauth));
        settings.Items.Add(new TabItem{Header="Bağlantı",Content=Scroll(form)});
        var categories=new DockPanel{Margin=new Thickness(8)};var categoryTop=new StackPanel();categoryTop.Children.Add(Note("Yerel kategorinin Etsy karşılığını bir kez seçin. Ürün kartındaki özel kategori seçimi önceliklidir."));
        categorySearch.Width=280;categorySearch.TextChanged+=(_,_)=>UpdateCategoryChoices();
        categoryTop.Children.Add(Bar(Field("Yerel kategori",localCategory),Field("Etsy kategorisinde ara",categorySearch),Field("Etsy kategorisi",categoryTarget)));categoryTarget.Width=420;
        categoryTop.Children.Add(Bar(ActionButton("Kategori eşlemesini kaydet",()=>{var local=Selected(localCategory);var target=Id(categoryTarget);if(local.Length==0||target is null)throw new InvalidOperationException("Yerel ve Etsy kategorisini seçin.");state.CategoryMappings.RemoveAll(m=>m.LocalCategory==local);state.CategoryMappings.Add(new(){LocalCategory=local,TaxonomyId=target.Value});SaveState();}),ActionButton("Seçili yerel eşlemeyi kaldır",()=>{if(mappings.SelectedItem is MappingRow row){state.CategoryMappings.RemoveAll(m=>m.LocalCategory==row.LocalCategory);SaveState();}})));
        DockPanel.SetDock(categoryTop,Dock.Top);categories.Children.Add(categoryTop);Column(mappings,"Yerel kategori","LocalCategory",380);Column(mappings,"Etsy kategori yolu","Target",520);categories.Children.Add(mappings);settings.Items.Add(new TabItem{Header="Kategori eşleme",Content=categories});
        var templates=new DockPanel{Margin=new Thickness(8)};var templateTop=new StackPanel();templateTop.Children.Add(Note("Kargo ve hazırlık süresi Etsy'deki mağaza profillerinden seçilir. Şablonu ürün listesinden seçili ürünlere atayın."));templateList.MinWidth=280;
        templateTop.Children.Add(Bar(templateList,ActionButton("Yeni şablon",()=>OpenTemplate(null)),ActionButton("Şablonu düzenle",()=>OpenTemplate(state.Templates.FirstOrDefault(t=>t.Id==Selected(templateList))??throw new InvalidOperationException("Şablon seçin."))),AsyncButton("Mağaza profillerini yenile",PullMetadata)));
        templateTop.Children.Add(Note("Hazırlayan, üretim zamanı ve ürünün malzeme/tedarik niteliği gerçek ürüne uygun seçilmelidir. Döviz dönüşümü otomatik varsayılmaz; Etsy fiyatı mağaza dövizinde girilir."));DockPanel.SetDock(templateTop,Dock.Top);templates.Children.Add(templateTop);templates.Children.Add(new TextBlock{Text="Kargo profillerini Etsy mağazanızda oluşturup buradan alın.\nHer ürün kendi başlık, açıklama, kategori ve özellikleriyle şablonu tamamlayabilir.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)});
        settings.Items.Add(new TabItem{Header="İlan ve kargo şablonları",Content=templates});
        if(scopedConnection is not null)settings.Items.Add(new TabItem{Header="Hesap kuralları",Content=new MarketplaceShopSettingsPanel(scopedConnection.Id,directory)});
        return settings;
    }
    EtsyCredentials ReadCredentials()
    {
        var prior=credentials??new("","","","");
        return EtsyCredentialChanges.Merge(prior,key.Password,secret.Password,token.Password,refreshToken.Password,shop.Text,redirect.Text);
    }
    void FillCredentials(EtsyCredentials value) {key.Password=value.Key;secret.Password=value.Secret;token.Password=value.Token;refreshToken.Password=value.RefreshToken;shop.Text=value.ShopId;redirect.Text=value.RedirectUri.Length>0?value.RedirectUri:"https://localhost:5099/etsy/callback";}
    void PersistCredentials(EtsyCredentials value)
    {
        if(scopedConnection is null) CredentialStore.Save(value,directory);
        else
        {
            var current=CurrentScopedConnection();
            if(value.ShopId!=current.ShopId)throw new InvalidOperationException("WRONG_ACCOUNT: Etsy mağaza kimliği seçili hesapla eşleşmiyor.");
            credentialVault!.Save(current.Id,current.Channel,current.ShopId,value);
        }
        credentials=value;FillCredentials(value);CredentialsChanged?.Invoke(value);ClearPreview();LoadState();
    }
    void SaveConnection()
    {
        var value=ReadCredentials();if(!long.TryParse(value.ShopId,out var id)||id<=0)throw new InvalidOperationException("Mağaza ID pozitif bir sayı olmalıdır.");EtsyHttp.ValidateValue(value.Key);EtsyHttp.ValidateValue(value.Secret);
        PersistCredentials(value);connectionStatus.Text="Erişim bilgileri şifreli kaydedildi. Bağlantıyı doğrulayın.";
    }
    async Task<EtsyCredentials> Authorized()
    {
        if(scopedConnection is not null)CurrentScopedConnection();
        var saved=credentials??throw new InvalidOperationException("Önce Etsy bağlantısını kaydedin.");
        if(string.IsNullOrEmpty(saved.Token))throw new InvalidOperationException("Etsy hesabını tarayıcı üzerinden yetkilendirin.");
        if(!saved.IsAccessTokenUsable()){saved=await new EtsyOAuth(http).RefreshAsync(saved,lifetime.Token);PersistCredentials(saved);}return saved;
    }
    async Task TestConnection()
    {
        var c=await Authorized();var info=await new EtsyMetadataClient(http).GetShopAsync(c,lifetime.Token);state=store.Load(c.ShopId);state.ShopName=info.Name;state.Currency=info.Currency;SaveState();connectionStatus.Text=$"Bağlı: {info.Name} · Mağaza {info.ShopId} · {info.Currency}";summary.Text=connectionStatus.Text;
    }
    async Task PullMetadata()
    {
        var c=await Authorized();var revision=state.Revision;var client=new EtsyMetadataClient(http);var info=await client.GetShopAsync(c,lifetime.Token);
        var categories=await client.GetSellerTaxonomyAsync(c,lifetime.Token);var shipping=await client.GetShippingProfilesAsync(c,lifetime.Token);var processing=await client.GetProcessingProfilesAsync(c,lifetime.Token);var sections=await client.GetSectionsAsync(c,lifetime.Token);
        if(credentials?.ShopId!=c.ShopId||state.Revision!=revision)throw new InvalidOperationException("Mağaza veya yerel ayarlar değişti; yeniden alın.");
        state.ShopId=c.ShopId;state.ShopName=info.Name;state.Currency=info.Currency;state.Categories=categories.ToList();state.ShippingProfiles=shipping.ToList();state.ProcessingProfiles=processing.ToList();state.Sections=sections.ToList();SaveState();
        connectionStatus.Text=$"{info.Name} · {categories.Count} kategori · {shipping.Count} kargo · {processing.Count} hazırlık profili · {sections.Count} bölüm";summary.Text=connectionStatus.Text;
    }
    async Task PullListings()
    {
        var c=await Authorized();var revision=state.Revision;await new EtsyMetadataClient(http).GetShopAsync(c,lifetime.Token);var client=new EtsyShopClient(http);var result=new Dictionary<long,EtsyListing>();
        foreach(var listingState in new[]{"active","draft","inactive","expired","sold_out"})
        {
            var offset=0;
            while(true){var part=await client.GetListingsAsync(c,listingState,offset,lifetime.Token);foreach(var listing in part.Listings)result[listing.ListingId]=listing;offset+=part.Listings.Count;if(offset>=part.Count||part.Listings.Count==0)break;if(offset>12000)throw new InvalidOperationException("İlan sayısı tek seferde okuma sınırını aşıyor.");}
        }
        if(credentials?.ShopId!=c.ShopId||state.Revision!=revision)throw new InvalidOperationException("Mağaza veya ayarlar değişti; yeniden alın.");
        state.Listings=result.Values.ToList();state.LastRefreshUtc=DateTime.UtcNow;SaveState();summary.Text=$"{state.ShopName} · {result.Count} Etsy ilanı okundu · Son kontrol {DateTime.Now:g}";
    }
    void ReloadChoices()
    {
        SetOptions(localCategory,catalog.Products().Select(p=>p.Category).Where(x=>x.Length>0).Distinct().Order().Select(x=>new Option(x,x)));
        UpdateCategoryChoices();SetOptions(templateList,state.Templates.Select(t=>new Option(t.Id,t.Name)));SetOptions(templateChoice,state.Templates.Select(t=>new Option(t.Id,t.Name)));
        mappings.ItemsSource=state.CategoryMappings.Select(m=>new MappingRow(m.LocalCategory,state.Categories.FirstOrDefault(c=>c.Id==m.TaxonomyId)?.Path??m.TaxonomyId.ToString())).ToList();
    }
    void UpdateCategoryChoices()=>SetOptions(categoryTarget,state.Categories.Where(c=>c.Path.Contains(categorySearch.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).Select(c=>new Option(c.Id.ToString(),c.Path)));
    sealed record MappingRow(string LocalCategory,string Target);
}
