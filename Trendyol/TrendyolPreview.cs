using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed partial class TrendyolWorkspaceStore
{
    public TrendyolPlan Preview(TrendyolSettings account, IEnumerable<string> productIds, TrendyolOperation operation, string? connectionId = null)
    {
        TrendyolConnection.Validate(account);
        var ids=productIds.Distinct().ToArray();
        if(ids.Length is <1 or >1000 || !Enum.IsDefined(operation)) throw new InvalidOperationException("Önizleme için 1–1000 ürün seçin.");
        if(connectionId is not null)
        {
            var connections=new MarketplaceConnectionStore(directory);var scoped=connections.Get(connectionId)??throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
            if(scoped.Channel!="trendyol"||scoped.ShopId!=account.SupplierId||!MarketplaceOperationalAccounts.IsEligible(scoped,connections))
                throw new InvalidOperationException("Hesap kapsamlı Trendyol bağlantısı önizleme hesabıyla eşleşmiyor.");
        }
        var accountBindings=connectionId is null?null:new ProductChannelBindingStore(directory);
        using var c=Open();using var tx=c.BeginTransaction();var state=Load(c,tx,account.SupplierId);
        var rows=new List<TrendyolPreviewRow>();
        foreach(var id in ids)
        {
            using var read=c.CreateCommand();read.Transaction=tx;read.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";read.Parameters.AddWithValue("$id",id);
            var json=read.ExecuteScalar() as string ?? throw new InvalidOperationException("Ürün bulunamadı.");
            var p=JsonSerializer.Deserialize<CatalogProduct>(json) ?? throw new InvalidOperationException("Ürün okunamadı.");
            if(p.Id!=id) throw new InvalidOperationException("Ürün kimliği tutarsız.");
            var profile=state.Profiles.SingleOrDefault(x=>x.ProductId==id) ?? new(){ProductId=id};
            var accountBinding=connectionId is null?null:accountBindings!.Get(id,connectionId);
            var integrationCode=profile.IntegrationCode.Length>0?profile.IntegrationCode:accountBinding?.RemoteBarcode??"";
            var listingBarcode=profile.ListingBarcode.Length>0?profile.ListingBarcode:accountBinding?.RemoteBarcode??"";
            var barcode=operation==TrendyolOperation.Create
                ? (listingBarcode.Length>0?listingBarcode:p.Barcode)
                : integrationCode;
            try
            {
                Fresh(state.ProductsUpdatedUtc,TimeSpan.FromHours(24),"Mağaza ürünlerini yenileyin");
                if(!p.Active) throw new InvalidOperationException("Yerel ürün pasif.");
                if(operation!=TrendyolOperation.Create&&profile.ListingBarcode.Length>0&&profile.ListingBarcode!=integrationCode)
                    throw new InvalidOperationException("Gönderilecek barkod mevcut mağaza eşleşmesinden farklı. Eski barkoda güncelleme gönderilmez; ürünü barkoduyla yeniden eşleştirin.");
                if(barcode.Length is <1 or >40 || barcode.Any(ch=>!char.IsLetterOrDigit(ch)&&ch!='.'&&ch!='-'&&ch!='_')) throw new InvalidOperationException("Ürün barkodu gerekli (en fazla 40 karakter, boşluksuz). Barkod girin; stok kodu, GTIN veya eski eşleşme kodu barkod yerine kullanılmaz.");
                var remote=state.Products.SingleOrDefault(x=>x.Barcode==barcode);
                if(accountBinding is not null && remote is not null &&
                    (accountBinding.RemoteId != remote.ContentId.ToString(CultureInfo.InvariantCulture) ||
                     (accountBinding.RemoteSku.Length>0 && accountBinding.RemoteSku != remote.StockCode)))
                    throw new InvalidOperationException("Hesap kapsamlı uzak ürün kimliği mağaza önbelleğiyle eşleşmiyor.");
                if(operation==TrendyolOperation.Create && remote!=null) throw new InvalidOperationException("Bu barkod mağazada var; ekleme yerine güncelleme seçin.");
                if(operation==TrendyolOperation.UpdateUnapproved && (integrationCode.Length==0 || remote is null || remote.Approved)) throw new InvalidOperationException("Önce onaysız mağaza ürünüyle eşleştirin.");
                if(operation is not (TrendyolOperation.Create or TrendyolOperation.UpdateUnapproved))
                {
                    if(integrationCode.Length==0 || remote is null)throw new InvalidOperationException("Önce onaylı mağaza ürünüyle eşleştirin.");
                    if(!remote.Approved)throw new InvalidOperationException("Ürün Trendyol'da henüz onaylı değil; mağaza durumunu kontrol edip ürün listesini yenileyin.");
                }
                var item=new Dictionary<string,object>{{"barcode",barcode}};
                if(operation is TrendyolOperation.Create or TrendyolOperation.Stock or TrendyolOperation.PriceAndStock)
                { if(p.Stock is <0 or >20000) throw new InvalidOperationException("Stok 0–20000 arasında olmalı.");item["quantity"]=p.Stock; }
                if(operation is TrendyolOperation.Create or TrendyolOperation.Price or TrendyolOperation.PriceAndStock)
                {
                    var channel=p.ChannelPrices.FirstOrDefault(x=>x.Key.Equals("trendyol",StringComparison.OrdinalIgnoreCase)).Value;
                    var sale=profile.SalePriceTry ?? (channel?.Currency=="TRY"?channel.SalePrice:p.Currency=="TRY"?p.Price:(decimal?)null);
                    var list=profile.ListPriceTry ?? (channel?.Currency=="TRY"?channel.ListPrice:null) ?? sale;
                    if(sale is null or <=0 || list<sale || decimal.Round(sale.Value,2)!=sale || decimal.Round(list!.Value,2)!=list) throw new InvalidOperationException("KDV dahil TRY satış/liste fiyatı gerekli; liste satıştan düşük olamaz, en fazla 2 ondalık kullanın.");
                    item["salePrice"]=sale.Value;item["listPrice"]=list!.Value;
                }
                var templateId=profile.DeliveryTemplateId.Length>0?profile.DeliveryTemplateId:accountBinding?.TemplateId??"";
                var template=state.Templates.SingleOrDefault(t=>t.Id==templateId);
                if(operation is TrendyolOperation.Create or TrendyolOperation.UpdateUnapproved)
                {
                    Fresh(state.DictionaryUpdatedUtc,TimeSpan.FromDays(7),"Kategori/marka listesini yenileyin");
                    var bindingCategory=long.TryParse(accountBinding?.CategoryId,NumberStyles.None,CultureInfo.InvariantCulture,out var parsedCategory)?parsedCategory:(long?)null;
                    var category=profile.CategoryId ?? bindingCategory ?? ResolveMapping(c,tx,state,TaxonomyKind.Category,p.Category);
                    var brand=profile.BrandId ?? ResolveMapping(c,tx,state,TaxonomyKind.Brand,p.Brand);
                    if(!state.Categories.Any(x=>x.Id==category&&x.IsLeaf)) throw new InvalidOperationException("Trendyol'un en alt kategorisini eşleştirin.");
                    if(!state.Brands.Any(x=>x.Id==brand)) throw new InvalidOperationException("Trendyol markasını eşleştirin.");
                    if(!state.Attributes.TryGetValue(category,out var definitions)) throw new InvalidOperationException("Kategorinin özelliklerini API'den yükleyin.");
                    Fresh(state.AttributesUpdatedUtc.GetValueOrDefault(category),TimeSpan.FromDays(7),"Kategori özelliklerini yenileyin");
                    var title=profile.Title.Length>0?profile.Title:p.Name;var description=profile.Description.Length>0?profile.Description:p.Description;
                    var model=profile.ModelCode.Length>0?profile.ModelCode:p.Sku;
                    if(string.IsNullOrWhiteSpace(title)||title.Length>100||string.IsNullOrWhiteSpace(description)||description.Length>30000||string.IsNullOrWhiteSpace(model)||model.Length>40||string.IsNullOrWhiteSpace(p.Sku)||p.Sku.Length>100) throw new InvalidOperationException("Başlık (100), açıklama (30000), model kodu (40) ve SKU (100) sınırlarını kontrol edin.");
                    var origin=profile.Origin.ToUpperInvariant();
                    if((origin.Length==0 && DateTime.UtcNow.Date>=new DateTime(2026,10,23)) || (origin.Length>0 && (origin.Length!=2 || !CultureInfo.GetCultures(CultureTypes.SpecificCultures).Any(culture=>new RegionInfo(culture.Name).TwoLetterISORegionName==origin)))) throw new InvalidOperationException("Geçerli iki harfli menşei girin (ör. TR, CN); 23.10.2026 itibarıyla zorunludur.");
                    var images=ProductAssetCache.Urls(p).ToArray();
                    if(images.Length is <1 or >8 || images.Any(url=>!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https"||!string.IsNullOrEmpty(u.UserInfo))) throw new InvalidOperationException("1–8 adet erişilebilir HTTPS görseli gerekli. Yerel arşiv yolu gönderilemez.");
                    if(p.VatRate is <0 or >100 || decimal.Truncate(p.VatRate)!=p.VatRate) throw new InvalidOperationException("KDV oranı tam sayı olmalı.");
                    item["title"]=title;item["description"]=description;item["productMainId"]=model;item["stockCode"]=p.Sku;if(origin.Length>0)item["origin"]=origin;item["brandId"]=brand;item["categoryId"]=category;item["vatRate"]=(int)p.VatRate;
                    item["images"]=images.Select(url=>new{url}).ToArray();item["attributes"]=Attributes(TrendyolProductSafety.Resolve(state,p,profile,definitions),definitions);
                    if(template?.IncludeProductDesi!=false) AddProductDesi(item,p);
                    if(template!=null) { AddShipping(item,template,state);if(template.DurationDays.HasValue)item["deliveryOption"]=new{deliveryDuration=template.DurationDays.Value}; }
                }
                if(operation==TrendyolOperation.Content)
                {
                    // Approved content is shared by every barcode under the same content ID.
                    // Send only explicit profile overrides, preserving absent fields and attributes.
                    if(state.Products.Count(x=>x.Approved&&x.ContentId==remote!.ContentId)>1)throw new InvalidOperationException("Bu içerik birden fazla barkodda ortak; paylaşılan içerik bu ekranda değiştirilmez.");
                    item.Clear();item["contentId"]=remote!.ContentId;
                    if(profile.Title.Length>0){if(p.LockName)throw new InvalidOperationException("Ürün adı XML'de kilitli.");if(profile.Title.Length>100)throw new InvalidOperationException("Başlık en fazla 100 karakter olabilir.");item["title"]=profile.Title;}
                    if(profile.Description.Length>0){if(p.LockDescription)throw new InvalidOperationException("Ürün açıklaması XML'de kilitli.");if(profile.Description.Length>30000)throw new InvalidOperationException("Açıklama en fazla 30000 karakter olabilir.");item["description"]=profile.Description;}
                    if(item.Count==1)throw new InvalidOperationException("Gönderilecek Trendyol başlığı veya açıklaması girip profili kaydedin. Boş alan korunur.");
                }
                if(operation is TrendyolOperation.Delivery or TrendyolOperation.ShippingDetails)
                {
                    if(template is null) throw new InvalidOperationException("Teslimat şablonu atayın.");
                    if(operation==TrendyolOperation.Delivery){if(!template.DurationDays.HasValue)throw new InvalidOperationException("Şablonda teslimat süresi seçin.");item["deliveryOptions"]=new{deliveryDuration=template.DurationDays.Value};}
                    else {AddShipping(item,template,state);if(template.IncludeProductDesi)AddProductDesi(item,p);if(item.Count==1)throw new InvalidOperationException("Ürün kartında desi girin veya şablonda kargo/adres seçin.");}
                }
                var unchanged=remote!=null && operation switch
                {
                    TrendyolOperation.Stock=>remote.Quantity==p.Stock,
                    TrendyolOperation.Price=>remote.SalePrice==(decimal)item["salePrice"] && remote.ListPrice==(decimal)item["listPrice"],
                    TrendyolOperation.PriceAndStock=>remote.Quantity==p.Stock && remote.SalePrice==(decimal)item["salePrice"] && remote.ListPrice==(decimal)item["listPrice"],
                    _=>false
                };
                var detail=operation switch {TrendyolOperation.Stock=>$"Stok: {remote?.Quantity} → {p.Stock}",TrendyolOperation.Price=>$"Satış: {remote?.SalePrice} → {item["salePrice"]} TRY; liste: {remote?.ListPrice} → {item["listPrice"]} TRY",TrendyolOperation.PriceAndStock=>$"Stok: {remote?.Quantity} → {p.Stock}; satış: {remote?.SalePrice} → {item["salePrice"]} TRY; liste: {remote?.ListPrice} → {item["listPrice"]}",TrendyolOperation.Delivery=>$"Termin: {template!.DurationDays} gün",TrendyolOperation.ShippingDetails=>$"Desi: {item.GetValueOrDefault("dimensionalWeight")??"gönderilmez"}; kargo: {template!.CarrierCode}; sevk/iade: {template.ShipmentAddressId}/{template.ReturningAddressId}",TrendyolOperation.UpdateUnapproved=>"Onaysız ürün bilgileri düzeltilecek; fiyat ve stok korunur",TrendyolOperation.Content=>"Dolu başlık/açıklama gönderilecek; diğer alanlar korunur",_=>$"Yeni ürün; stok kodu: {p.Sku}; barkod: {barcode}; desi: {item.GetValueOrDefault("dimensionalWeight")??"girilmemiş"}; termin: {(template?.DurationDays.HasValue==true?template.DurationDays+" gün":"mağaza varsayılanı")}"};
                rows.Add(new(id,p.Sku,p.Name,barcode,unchanged?"Atlanacak":operation==TrendyolOperation.Create?"Eklenecek":"Güncellenecek",unchanged?"Mağaza önbelleği ile aynı; değişiklik yok":detail,unchanged?null:JsonSerializer.Serialize(item)));
            }
            catch(InvalidOperationException ex){rows.Add(new(id,p.Sku,p.Name,barcode,"Hatalı",ex.Message,null));}
        }
        var duplicates=rows.Where(r=>r.ItemJson!=null).GroupBy(r=>r.Barcode).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        rows=rows.Select(r=>duplicates.Contains(r.Barcode)?r with{Status="Hatalı",Detail="Birden fazla yerel ürün aynı Trendyol barkodunu hedefliyor.",ItemJson=null}:r).ToList();
        var payload=JsonSerializer.Serialize(new{items=rows.Where(r=>r.ItemJson!=null).Select(r=>JsonSerializer.Deserialize<JsonElement>(r.ItemJson!)).ToArray()});
        var plan=new TrendyolPlan(Guid.NewGuid().ToString("N"),account.SupplierId,AccountFingerprint(account),state.Revision,DateTime.UtcNow,operation,CatalogFingerprint(c,tx,ids),rows,payload);
        using var save=c.CreateCommand();save.Transaction=tx;save.CommandText="INSERT INTO TrendyolPlans VALUES($id,$seller,$json)";save.Parameters.AddWithValue("$id",plan.Id);save.Parameters.AddWithValue("$seller",plan.SellerId);save.Parameters.AddWithValue("$json",JsonSerializer.Serialize(plan));save.ExecuteNonQuery();tx.Commit();return plan;
    }
    static void Fresh(DateTime? time,TimeSpan limit,string message){if(time is null || DateTime.UtcNow-time.Value>limit || time>DateTime.UtcNow.AddMinutes(1))throw new InvalidOperationException(message+"; eski veriye göre gönderim yapılamaz.");}
    static void AddProductDesi(Dictionary<string,object> item,CatalogProduct product)
    {
        var text=product.XmlAttributes.GetValueOrDefault("Desi","").Trim();
        if(text.Length==0)return;
        // Accept a decimal comma, but never interpret it as a thousands separator.
        if(!decimal.TryParse(text.Replace(',','.'),NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var weight)||weight<0)
            throw new InvalidOperationException("Desi geçersiz. Ürün kartında sıfır veya pozitif sayı girin (ör. 2,5); binlik ayırıcı kullanmayın.");
        item["dimensionalWeight"]=weight;
    }
    static long ResolveMapping(SqliteConnection c,SqliteTransaction tx,TrendyolWorkspaceState state,TaxonomyKind kind,string name)
    {
        var map=state.Mappings.SingleOrDefault(m=>m.Kind==kind&&(kind==TaxonomyKind.Category?TaxonomyStore.SameCategory(m.LocalName,name):TrendyolMatching.Normalize(m.LocalName)==TrendyolMatching.Normalize(name)));if(map is null)return 0;
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Name FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind AND Active=1";cmd.Parameters.AddWithValue("$id",map.LocalId);cmd.Parameters.AddWithValue("$kind",(int)kind);
        return cmd.ExecuteScalar() as string==map.LocalName?map.RemoteId:0;
    }
    static object[] Attributes(List<TrendyolAttributeSelection> selections,List<TrendyolAttribute> definitions)
    {
        var result=new List<object>();
        if(selections.Any(a=>!definitions.Any(d=>d.Id==a.AttributeId)))throw new InvalidOperationException("Kategoride bulunmayan özellik seçilmiş.");
        foreach(var definition in definitions)
        {
            var selected=selections.SingleOrDefault(a=>a.AttributeId==definition.Id);
            if(selected is null || (selected.ValueIds.Length==0 && selected.CustomValue.Length==0)){if(definition.Required)throw new InvalidOperationException("Zorunlu özellik eksik: "+definition.Name);continue;}
            if(selected.CustomValue.Length>0){if(!definition.AllowCustom||selected.ValueIds.Length>0||(definition.Id==47&&selected.CustomValue.Length>50))throw new InvalidOperationException("Serbest özellik değeri geçersiz: "+definition.Name);result.Add(new{attributeId=definition.Id,customAttributeValue=selected.CustomValue});}
            else {if(selected.ValueIds.Distinct().Count()!=selected.ValueIds.Length||selected.ValueIds.Any(id=>!definition.Values.Any(v=>v.Id==id))||selected.ValueIds.Length>1&&!definition.AllowMultiple)throw new InvalidOperationException("Özellik değeri geçersiz: "+definition.Name);result.Add(selected.ValueIds.Length==1?(object)new{attributeId=definition.Id,attributeValueId=selected.ValueIds[0]}:new{attributeId=definition.Id,attributeValueIds=selected.ValueIds});}
        }
        return result.ToArray();
    }
    static void AddShipping(Dictionary<string,object> item,TrendyolDeliveryTemplate template,TrendyolWorkspaceState state)
    {
        if(template.CarrierCode.Length>0||template.ShipmentAddressId.HasValue||template.ReturningAddressId.HasValue)Fresh(state.AddressesUpdatedUtc,TimeSpan.FromDays(7),"Kargo ve adresleri yenileyin");
        if(template.CarrierCode.Length>0){if(!state.Carriers.Any(c=>c.Code==template.CarrierCode))throw new InvalidOperationException("Kargo sağlayıcı kodu güncel listede yok.");item["cargoProviders"]=new[]{template.CarrierCode};}
        if(template.ShipmentAddressId.HasValue){if(!state.Addresses.Any(a=>a.Id==template.ShipmentAddressId&&a.IsShipment))throw new InvalidOperationException("Sevkiyat adresi geçersiz.");item["shipmentAddressId"]=template.ShipmentAddressId.Value;}
        if(template.ReturningAddressId.HasValue){if(!state.Addresses.Any(a=>a.Id==template.ReturningAddressId&&a.IsReturning))throw new InvalidOperationException("İade adresi geçersiz.");item["returningAddressId"]=template.ReturningAddressId.Value;}
    }
}
