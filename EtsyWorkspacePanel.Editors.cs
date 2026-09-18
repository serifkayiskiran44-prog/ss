using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Etsy;

namespace TrMarketplaceHubDesktop;
public sealed partial class EtsyWorkspacePanel
{
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    ComboBox CategoryPicker(long? id=null)
    {var combo=Select();combo.Width=550;SetOptions(combo,new[]{new Option("","Şablon / kategori eşlemesinden al")}.Concat(state.Categories.Select(c=>new Option(c.Id.ToString(),c.Path))),id?.ToString()??"");return combo;}
    ComboBox ShippingPicker(long? id=null)
    {var combo=Select();SetOptions(combo,new[]{new Option("","Şablondan al")}.Concat(state.ShippingProfiles.Select(c=>new Option(c.Id.ToString(),c.Name))),id?.ToString()??"");return combo;}
    ComboBox ProcessingPicker(long? id=null)
    {var combo=Select();SetOptions(combo,new[]{new Option("","Şablondan al")}.Concat(state.ProcessingProfiles.Select(c=>new Option(c.Id.ToString(),$"{c.Name} · {c.ReadinessState}"))),id?.ToString()??"");return combo;}
    ComboBox SectionPicker(long? id=null)
    {var combo=Select();SetOptions(combo,new[]{new Option("","Şablon / mağaza varsayılanı")}.Concat(state.Sections.Select(c=>new Option(c.Id.ToString(),c.Name))),id?.ToString()??"");return combo;}
    void OpenTemplate(EtsyWorkspaceTemplate? existing)
    {
        if(string.IsNullOrEmpty(state.ShopId))throw new InvalidOperationException("Önce mağaza bağlantısını doğrulayın.");
        var model=existing is null?new EtsyWorkspaceTemplate{Listing=new(){Currency=state.Currency}}:Copy(existing);
        var window=Dialog("Etsy ilan ve kargo şablonu",940,680);var form=new StackPanel();var name=Input(model.Name);name.Width=400;
        var currency=Input(state.Currency);currency.IsReadOnly=true;var who=Select();SetOptions(who,new[]{new Option("","Seçin"),new Option("i_did","Ben yaptım"),new Option("collective","Birlikte yaptık"),new Option("someone_else","Başka kişi / kuruluş yaptı")},model.Listing.WhoMade);
        var when=Select();SetOptions(when,new[]{new Option("","Seçin")}.Concat(EtsyDrafts.WhenMadeValues.Select(x=>new Option(x,x=="made_to_order"?"Sipariş üzerine":x.Replace('_','–')))),model.Listing.WhenMade);
        var supply=Choices("Bitmiş ürün","Üretim malzemesi / araç");supply.SelectedIndex=model.Listing.IsSupply?1:0;
        var shipping=ShippingPicker(model.Listing.ShippingProfileId);var processing=ProcessingPicker(model.Listing.ReadinessStateId);var section=SectionPicker(model.ShopSectionId);var category=CategoryPicker(model.Listing.TaxonomyId);var categoryQuery=Input();categoryQuery.Width=400;
        categoryQuery.TextChanged+=(_,_)=>SetOptions(category,new[]{new Option("","Üründen / eşlemeden al")}.Concat(state.Categories.Where(c=>c.Path.Contains(categoryQuery.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).Select(c=>new Option(c.Id.ToString(),c.Path))));
        var prefix=Input(model.Listing.TitlePrefix);var tags=Input(model.Listing.Tags);tags.Width=500;var materials=Input(model.Listing.Materials);materials.Width=500;
        form.Children.Add(Bar(Field("Şablon adı",name),Field("Mağaza dövizi",currency)));form.Children.Add(Bar(Field("Kim yaptı?",who),Field("Ne zaman yapıldı?",when),Field("Ürün türü",supply)));
        form.Children.Add(Bar(Field("Kargo profili",shipping),Field("Hazırlık / termin süresi",processing),Field("Mağaza bölümü",section)));
        form.Children.Add(Field("Kategori ara",categoryQuery));form.Children.Add(Field("Varsayılan Etsy kategorisi",category));form.Children.Add(Field("Başlık ön eki (isteğe bağlı)",prefix));form.Children.Add(Field("Etiketler (virgülle ayırın, en fazla 13)",tags));form.Children.Add(Field("Malzemeler (virgülle ayırın)",materials));
        form.Children.Add(Note("Bu kayıt yalnız yerel şablonu değiştirir. Ürünleri seçip şablonu atadıktan sonra önizleyin."));
        var dock=new DockPanel();var save=ActionButton("Şablonu kaydet",()=>
        {
            if(string.IsNullOrWhiteSpace(name.Text))throw new InvalidOperationException("Şablon adı girin.");
            model.Name=name.Text.Trim();model.Listing.Currency=state.Currency;model.Listing.WhoMade=Selected(who);model.Listing.WhenMade=Selected(when);model.Listing.IsSupply=supply.SelectedIndex==1;model.Listing.ShippingProfileId=Id(shipping)??0;model.Listing.ReadinessStateId=Id(processing)??0;model.Listing.TaxonomyId=Id(category)??0;model.Listing.Tags=tags.Text.Trim();model.Listing.Materials=materials.Text.Trim();model.Listing.TitlePrefix=prefix.Text.Trim();model.ShopSectionId=Id(section);
            var candidate=Copy(state);candidate.Templates.RemoveAll(t=>t.Id==model.Id);candidate.Templates.Add(model);store.Save(candidate);state=store.Load(candidate.ShopId);ClearPreview();ReloadChoices();RefreshProducts();window.Close();
        });DockPanel.SetDock(save,Dock.Bottom);dock.Children.Add(save);dock.Children.Add(Scroll(form));window.Content=dock;window.ShowDialog();
    }
    void OpenProduct()
    {
        if(products.SelectedItem is not ProductRow row)throw new InvalidOperationException("Ürün listesinden bir ürün seçin.");
        if(string.IsNullOrEmpty(state.ShopId))throw new InvalidOperationException("Önce mağaza bağlantısını kaydedin.");
        var product=catalog.Products().Single(p=>p.Id==row.Id);var saved=state.Profiles.FirstOrDefault(p=>p.ProductId==product.Id);var model=saved is null?new EtsyProductProfile{ProductId=product.Id}:Copy(saved);
        var window=Dialog($"Etsy ürün kartı — {product.Sku}",1030,770);var dock=new DockPanel();var tabs=new TabControl();
        var general=new StackPanel();general.Children.Add(Note($"{product.Sku} · {product.Name}\nYerel barkod: {product.Barcode} · Stok: {product.Stock} · Yerel fiyat: {product.Price} {product.Currency}"));
        general.Children.Add(Note("Boş alanlar ürün / şablon bilgilerini kullanır. Kayıt yalnız Etsy profilini değiştirir; ürünün SKU ve barkodu korunur."));
        var template=Select();template.Width=320;SetOptions(template,new[]{new Option("","Şablon seçin")}.Concat(state.Templates.Select(t=>new Option(t.Id,t.Name))),model.TemplateId);
        var title=Input(model.Title);title.Width=700;title.MaxLength=140;var description=Input(model.Description);description.AcceptsReturn=true;description.Height=155;description.TextWrapping=TextWrapping.Wrap;description.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        var price=Input(model.Price?.ToString(CultureInfo.CurrentCulture)??"");var tags=Input(model.Tags);tags.Width=700;var materials=Input(model.Materials);materials.Width=700;
        var remote=Select();remote.Width=700;SetOptions(remote,new[]{new Option("","Eşleşme yok")}.Concat(state.Listings.Select(l=>new Option(l.ListingId.ToString(),$"{l.ListingId} · {l.Sku} · {l.Title} ({StateLabel(l.State)})"))),model.ListingId?.ToString()??"");
        general.Children.Add(Field("İlan şablonu",template));general.Children.Add(Field("Manuel Etsy ilan eşlemesi",remote));general.Children.Add(Field("Etsy başlığı (boş: ürün adı)",title));general.Children.Add(Field($"Etsy satış fiyatı ({state.Currency}; boş: yerel fiyat ve dövizi)",price));general.Children.Add(Field("Etsy açıklaması (boş: ürün açıklaması)",description));general.Children.Add(Field("Etiketler (virgülle ayırın)",tags));general.Children.Add(Field("Malzemeler (virgülle ayırın)",materials));tabs.Items.Add(new TabItem{Header="Ürün bilgileri",Content=Scroll(general)});
        var propertiesForm=new StackPanel();var category=CategoryPicker(model.TaxonomyId);var query=Input();query.Width=500;query.TextChanged+=(_,_)=>SetOptions(category,new[]{new Option("","Şablon / kategori eşlemesinden al")}.Concat(state.Categories.Where(c=>c.Path.Contains(query.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).Select(c=>new Option(c.Id.ToString(),c.Path))));
        var propertyControls=new StackPanel();var propertyHint=Note("Kategori seçip özellikleri alın. Zorunlu değerler yıldızla gösterilir; program değer tahmin etmez.");
        var editors=new List<PropertyEditor>();long? loadedCategory=null;
        propertiesForm.Children.Add(Field("Kategori ara",query));propertiesForm.Children.Add(Field("Etsy kategorisi",category));propertiesForm.Children.Add(propertyHint);
        long EffectiveCategory()=>Id(category)??state.CategoryMappings.FirstOrDefault(m=>m.LocalCategory==product.Category)?.TaxonomyId??state.Templates.FirstOrDefault(t=>t.Id==Selected(template))?.Listing.TaxonomyId??0;
        propertiesForm.Children.Add(AsyncButton("Kategori özelliklerini al",async()=>
        {
            var categoryId=EffectiveCategory();if(categoryId<=0)throw new InvalidOperationException("Kategori veya kategori içeren şablon seçin.");var c=await Authorized();var definitions=await new EtsyMetadataClient(http).GetPropertiesAsync(c,categoryId,lifetime.Token);
            propertyControls.Children.Clear();editors.Clear();loadedCategory=categoryId;
            foreach(var definition in definitions.Where(d=>d.SupportsAttributes))
            {
                var value=model.Properties.FirstOrDefault(p=>p.PropertyId==definition.Id);var selector=Select();selector.Width=420;SetOptions(selector,new[]{new Option("","Değer seçin")}.Concat(definition.Values.Select(v=>new Option(v.Id.ToString(),v.Name))),value?.ValueIds.FirstOrDefault().ToString()??"");
                var text=Input(value is null?"":string.Join(", ",value.Values));text.Width=420;
                var scale=Select();SetOptions(scale,new[]{new Option("","Ölçek seçin")}.Concat(definition.Scales.Select(s=>new Option(s.Id.ToString(),s.Name))),value?.ScaleId?.ToString()??"");
                FrameworkElement control=definition.Values.Count>0?selector:text;var bar=Bar(control);if(definition.Scales.Count>0)bar.Children.Add(scale);
                propertyControls.Children.Add(Field(definition.Name+(definition.Required?" *":""),bar));editors.Add(new(definition,selector,text,scale));
            }
            propertyHint.Text=$"{definitions.Count(d=>d.SupportsAttributes)} özellik · {definitions.Count(d=>d.Required)} zorunlu. Boş değerler gönderimde ayrıca kontrol edilir.";
        }));propertiesForm.Children.Add(propertyControls);tabs.Items.Add(new TabItem{Header="Kategori ve özellikler",Content=Scroll(propertiesForm)});
        var shipping=ShippingPicker(model.ShippingProfileId);var processing=ProcessingPicker(model.ReadinessStateId);var section=SectionPicker(model.ShopSectionId);var delivery=new StackPanel();delivery.Children.Add(Note("Boş seçim şablondaki profili kullanır. Hazırlık süresi Etsy mağazanızda tanımlı profile bağlıdır."));delivery.Children.Add(Field("Kargo profili",shipping));delivery.Children.Add(Field("Hazırlık / termin profili",processing));delivery.Children.Add(Field("Mağaza bölümü",section));tabs.Items.Add(new TabItem{Header="Kargo ve hazırlık",Content=Scroll(delivery)});
        var save=ActionButton("Etsy ürün profilini kaydet",()=>
        {
            var listingId=Id(remote);if(listingId.HasValue&&state.Profiles.Any(p=>p.ProductId!=product.Id&&p.ListingId==listingId))throw new InvalidOperationException("Bu Etsy ilanı başka bir ürüne bağlı.");
            decimal? amount=null;if(!string.IsNullOrWhiteSpace(price.Text)){if(!decimal.TryParse(price.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var number)||number<=0)throw new InvalidOperationException("Etsy satış fiyatını sıfırdan büyük bir sayı olarak girin.");amount=number;}
            model.TemplateId=Selected(template);model.ListingId=listingId;model.Title=title.Text.Trim();model.Description=description.Text.Trim();model.Price=amount;model.PriceCurrency=amount.HasValue?state.Currency:"";model.Tags=tags.Text.Trim();model.Materials=materials.Text.Trim();model.TaxonomyId=Id(category);model.ShippingProfileId=Id(shipping);model.ReadinessStateId=Id(processing);model.ShopSectionId=Id(section);
            var effective=EffectiveCategory();if(loadedCategory.HasValue&&loadedCategory!=effective)throw new InvalidOperationException("Kategori değişti; özellikleri yeniden alın.");
            if(loadedCategory.HasValue)model.Properties=editors.Select(e=>new EtsyProductProperty{PropertyId=e.Definition.Id,ScaleId=Id(e.Scale),ValueIds=Id(e.Selector) is long id?[id]:[],Values=e.Definition.Values.Count>0?[]:e.Text.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)}).Where(p=>p.ValueIds.Length>0||p.Values.Length>0).ToList();
            else if(saved?.TaxonomyId!=model.TaxonomyId)model.Properties=[];
            var candidate=Copy(state);candidate.Profiles.RemoveAll(p=>p.ProductId==model.ProductId);candidate.Profiles.Add(model);store.Save(candidate);state=store.Load(candidate.ShopId);ClearPreview();RefreshProducts();window.Close();
        });DockPanel.SetDock(save,Dock.Bottom);dock.Children.Add(save);dock.Children.Add(tabs);window.Content=dock;window.ShowDialog();
    }
    sealed record PropertyEditor(EtsyPropertyDefinition Definition,ComboBox Selector,TextBox Text,ComboBox Scale);
}
