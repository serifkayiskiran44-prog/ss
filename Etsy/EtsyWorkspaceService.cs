using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Etsy;

public sealed partial class EtsyWorkspaceService(string? directory, HttpClient http)
{
    readonly EtsyWorkspaceStore store = new(directory);
    readonly CatalogStore catalog = new(directory);
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented=true };
    static string Text(string? value,string fallback)=>string.IsNullOrWhiteSpace(value)?fallback:value.Trim();
    static string Number(object value)=>Convert.ToString(value,CultureInfo.InvariantCulture)!;
    static string Account(EtsyCredentials c,long user)=>EtsyWorkspaceStore.Hash(JsonSerializer.Serialize(new object[]{c.ShopId,c.Key,user,(c.GrantedScopes??[]).Order(StringComparer.Ordinal).ToArray()}));
    static void Credentials(EtsyCredentials c)
    {
        EtsyWorkspaceStore.Shop(c.ShopId);
        if(!c.IsAccessTokenUsable())throw new InvalidOperationException("Etsy oturumunu yenileyin.");
        if(c.GrantedScopes is not null&&(!c.GrantedScopes.Contains("listings_r")||!c.GrantedScopes.Contains("listings_w")))throw new InvalidOperationException("Etsy ilan okuma/yazma yetkileri eksik; bağlantıyı yeniden yetkilendirin.");
        // Etsy OAuth tokens carry the authorized numeric user ID before the dot.
        var prefix=c.Token.Split('.')[0]; if(!long.TryParse(prefix,NumberStyles.None,CultureInfo.InvariantCulture,out var user)||user<=0)throw new InvalidOperationException("Etsy yetkili kullanıcı kimliği doğrulanamadı; OAuth bağlantısını yenileyin.");
    }
    public IReadOnlyList<EtsyMatchRow> Match(EtsyWorkspaceState state,IReadOnlyList<CatalogProduct> products)
    {
        var all=catalog.Products(); var result=new List<EtsyMatchRow>();
        foreach(var p in products)
        {
            var hits=state.Listings.Where(l=>(l.Skus.Count==1||l.Skus.Count==0&&!l.Sku.Contains(", ",StringComparison.Ordinal))&&string.Equals((l.Skus.Count==1?l.Skus[0]:l.Sku).Trim(),p.Sku.Trim(),StringComparison.OrdinalIgnoreCase)).ToList();
            var unique=!string.IsNullOrWhiteSpace(p.Sku)&&hits.Count==1&&all.Count(x=>string.Equals(x.Sku.Trim(),p.Sku.Trim(),StringComparison.OrdinalIgnoreCase))==1;
            var available=unique&&!state.Profiles.Any(x=>x.ProductId!=p.Id&&x.ListingId==hits[0].ListingId);
            result.Add(new() { ProductId=p.Id,Sku=p.Sku,Title=p.Name,ListingId=available?hits[0].ListingId:null,CanMatch=available,Status=available?"Eşleşti":hits.Count==0?"Bulunamadı":"Çakışma",Detail=available?"Tam SKU eşleşmesi":"Benzersiz tam SKU bulunamadı veya ilan başka ürüne bağlı." });
        }
        return result;
    }
    public void ApplyMatches(EtsyWorkspaceState state,IReadOnlyList<EtsyMatchRow> matches)=>store.ApplyMatches(state,matches);

    public async Task<EtsyOperationPlan> PreviewAsync(EtsyCredentials credentials,IReadOnlyList<string> productIds,EtsyOperation operation,CancellationToken cancellationToken=default)
    {
        Credentials(credentials);
        if(productIds.Count is <1 or >500||productIds.Distinct().Count()!=productIds.Count||!Enum.IsDefined(operation))throw new InvalidOperationException("1–500 benzersiz ürün ve geçerli işlem seçin.");
        var state=store.Load(credentials.ShopId); var hash=store.CatalogHash(productIds); var products=catalog.Products().ToDictionary(p=>p.Id);
        var shop=await new EtsyMetadataClient(http).GetShopAsync(credentials,cancellationToken).ConfigureAwait(false);
        var rows=new List<EtsyPreviewRow>();
        foreach(var id in productIds)
        {
            cancellationToken.ThrowIfCancellationRequested(); var row=new EtsyPreviewRow { ProductId=id,Action=operation.ToString() }; rows.Add(row);
            try
            {
                if(!products.TryGetValue(id,out var product))throw new InvalidOperationException("Ürün katalogda bulunamadı.");
                row.Sku=product.Sku; row.Title=product.Name;
                if(store.HasUnresolved(state.ShopId,id))throw new InvalidOperationException("Önceki gönderimin sonucu belirsiz/eksik; Etsy mağazasında kontrol edip işlem geçmişini uzlaştırın. Tekrar gönderilmez.");
                var profile=state.Profiles.SingleOrDefault(p=>p.ProductId==id)??new() { ProductId=id };
                row.ListingId=profile.ListingId;
                if(!row.ListingId.HasValue&&!string.IsNullOrWhiteSpace(product.EtsyListingId))
                { if(!long.TryParse(product.EtsyListingId,NumberStyles.None,CultureInfo.InvariantCulture,out var legacy)||legacy<=0)throw new InvalidOperationException("Eski Etsy ilan kimliği geçersiz."); row.ListingId=legacy; }
                if(row.ListingId.HasValue&&state.Profiles.Any(p=>p.ProductId!=id&&p.ListingId==row.ListingId))throw new InvalidOperationException("İlan başka ürüne bağlı.");
                if(operation!=EtsyOperation.Deactivate&&!product.Active)throw new InvalidOperationException("Pasif ürün bu işlemle gönderilemez.");
                JsonElement remote=default;
                if(row.ListingId is long listing)
                {
                    remote=await Listing(credentials,listing,cancellationToken).ConfigureAwait(false); row.RemoteFingerprint=ListingHash(remote);
                    if(remote.GetProperty("state").GetString() is not ("active" or "inactive" or "draft" or "expired" or "sold_out"))throw new InvalidOperationException("Etsy ilan durumu bu işlem için uygun değil.");
                }
                if(operation==EtsyOperation.CreateDraft)
                {
                    if(row.ListingId.HasValue||product.EtsyCreationAttempted||store.Receipts(state.ShopId).Any(r=>r.ProductId==id&&r.ListingId.HasValue))throw new InvalidOperationException("Ürün için Etsy ilanı/oluşturma kaydı mevcut; tekrar taslak oluşturulamaz.");
                    await DraftOrContent(credentials,state,shop,product,profile,row,true,cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if(row.ListingId is not >0)throw new InvalidOperationException("Önce ürünü bir Etsy ilanıyla eşleştirin.");
                    var path=$"shops/{state.ShopId}/listings/{row.ListingId}";
                    if(operation is EtsyOperation.Price or EtsyOperation.Stock or EtsyOperation.PriceAndStock)
                    {
                        var inventory=await Get(credentials,$"listings/{row.ListingId}/inventory",cancellationToken).ConfigureAwait(false);
                        var body=SimpleInventory(inventory); row.InventoryFingerprint=InventoryHash(inventory);
                        var offering=body["products"]![0]!["offerings"]![0]!;
                        if(operation is EtsyOperation.Price or EtsyOperation.PriceAndStock)
                        {
                            var price=profile.Price??product.Price;
                            var currency=profile.Price.HasValue?profile.PriceCurrency:product.Currency;
                            if(price<=0||!SameCurrency(currency,shop.Currency)||!SameCurrency(currency,remote.GetProperty("price").GetProperty("currency_code").GetString()))throw new InvalidOperationException("Fiyatın açık para birimi, mağaza ve ilan para birimi aynı olmalı; döviz varsayılmaz.");
                            var invCurrency=inventory.GetProperty("products")[0].GetProperty("offerings")[0].GetProperty("price").GetProperty("currency_code").GetString();
                            if(!SameCurrency(invCurrency,shop.Currency))throw new InvalidOperationException("Etsy envanter para birimi mağazayla uyuşmuyor.");
                            offering["price"]=price;
                        }
                        if(operation is EtsyOperation.Stock or EtsyOperation.PriceAndStock)
                        { if(product.Stock<=0)throw new InvalidOperationException("Sıfır stok için ayrı Pasife al önizlemesini kullanın."); offering["quantity"]=product.Stock; }
                        row.Steps.Add(new() { Method="PUT",Path=$"listings/{row.ListingId}/inventory",Json=body.ToJsonString() });
                        row.Detail=$"Fiyat: {offering["price"]} {shop.Currency}; stok: {offering["quantity"]}. Seçilmeyen fiyat/stok, SKU, özellikler ve hazırlık profili mevcut Etsy değerleriyle korunur.";
                    }
                    else if(operation==EtsyOperation.Content)await DraftOrContent(credentials,state,shop,product,profile,row,false,cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var active=operation==EtsyOperation.Publish;
                        if(active&&(remote.GetProperty("state").GetString()=="sold_out"||remote.GetProperty("quantity").GetInt32()<=0))throw new InvalidOperationException("Tükenmiş ilan yayınlanamaz; önce ayrı stok işlemini tamamlayın.");
                        if(active&&remote.TryGetProperty("images",out var images)&&images.GetArrayLength()==0)throw new InvalidOperationException("Yayın için Etsy ilanına görsel ekleyin.");
                        row.Steps.Add(new() { Method="PATCH",Path=path,Form=new() { ["state"]=active?"active":"inactive" } });
                        row.Detail=active?"İlan Etsy'de yayına alınır; Etsy listeleme/yenileme ücreti uygulayabilir.":"İlan Etsy'de satıştan kaldırılır.";
                    }
                }
                row.PayloadJson=JsonSerializer.Serialize(row.Steps.Select(s=>new { s.Method,s.Path,Body=s.Json.Length>0?(object)JsonSerializer.Deserialize<JsonElement>(s.Json):s.Form,ImageBytes=s.Image?.Length,ImageSha256=s.Image is null?null:Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s.Image)) }),Pretty);
                row.CanSend=true;
            }
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or KeyNotFoundException or JsonException or FormatException)
            { row.CanSend=false; row.Steps.Clear(); row.Detail=ex is InvalidOperationException?ex.Message:"Etsy ürün/yanıt alanları geçersiz; gönderim hazırlanmadı."; }
        }
        // Two selected local products must never target the same remote listing.
        foreach(var group in rows.Where(r=>r.ListingId.HasValue).GroupBy(r=>r.ListingId).Where(g=>g.Count()>1))foreach(var row in group) { row.CanSend=false; row.Detail="Aynı Etsy ilanına birden fazla yerel ürün bağlı."; row.Steps.Clear(); }
        var plan=new EtsyOperationPlan { ShopId=state.ShopId,Operation=operation,Rows=rows,WorkspaceRevision=state.Revision,CatalogFingerprint=hash,AccountFingerprint=Account(credentials,shop.UserId),ShopCurrency=shop.Currency };
        store.Persist(plan); return plan;
    }
    static bool SameCurrency(string? a,string? b)=>!string.IsNullOrWhiteSpace(a)&&!string.IsNullOrWhiteSpace(b)&&string.Equals(a.Trim(),b.Trim(),StringComparison.OrdinalIgnoreCase);
    async Task<JsonElement> Get(EtsyCredentials c,string path,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://openapi.etsy.com/v3/application/"+path); EtsyHttp.AddHeaders(request,c,true);
        using var doc=await EtsyHttp.SendJsonAsync(http,request,8*1024*1024,ct).ConfigureAwait(false); return doc.RootElement.Clone();
    }
    async Task<JsonElement> Listing(EtsyCredentials c,long id,CancellationToken ct)
    {
        var root=await Get(c,$"listings/{id}",ct).ConfigureAwait(false);
        if(root.GetProperty("listing_id").GetInt64()!=id||Number(root.GetProperty("shop_id").GetInt64())!=c.ShopId)throw new InvalidOperationException("Etsy ilanı bağlı mağazaya ait değil; yazma engellendi.");
        return root;
    }
    static string ListingHash(JsonElement listing)
    {
        var fields=new SortedDictionary<string,JsonElement>();
        foreach(var name in new[]{"listing_id","shop_id","title","description","state","quantity","price","skus","last_modified_timestamp","updated_timestamp","taxonomy_id","tags","materials","shipping_profile_id","readiness_state_id","shop_section_id","who_made","when_made","is_supply","image_ids","images"})if(listing.TryGetProperty(name,out var value))fields[name]=value;
        return EtsyWorkspaceStore.Hash(JsonSerializer.Serialize(fields));
    }
    static string InventoryHash(JsonElement inventory)=>EtsyWorkspaceStore.Hash(SimpleInventory(inventory).ToJsonString());
    internal static JsonObject SimpleInventory(JsonElement inventory)
    {
        var products=inventory.GetProperty("products");
        if(products.GetArrayLength()!=1||products[0].GetProperty("offerings").GetArrayLength()!=1)throw new InvalidOperationException("Karmaşık Etsy envanteri bu işlemde desteklenmiyor; varyant yazımı kapalı.");
        var product=products[0]; var offering=product.GetProperty("offerings")[0];
        foreach(var item in new[]{product,offering})if(item.TryGetProperty("is_deleted",out var deleted)&&deleted.GetBoolean())throw new InvalidOperationException("Silinmiş Etsy envanterine yazılamaz.");
        var result=new JsonObject();
        foreach(var name in new[]{"price_on_property","quantity_on_property","sku_on_property","readiness_state_on_property"})
        {
            if(inventory.TryGetProperty(name,out var values)&&values.ValueKind==JsonValueKind.Array&&values.GetArrayLength()>0)throw new InvalidOperationException("Özelliğe bağlı Etsy envanter yazımı desteklenmiyor.");
            result[name]=inventory.TryGetProperty(name,out values)?JsonNode.Parse(values.GetRawText()):new JsonArray();
        }
        var money=offering.GetProperty("price"); var divisor=money.GetProperty("divisor").GetDecimal(); if(divisor<=0)throw new InvalidOperationException("Etsy fiyatı geçersiz.");
        var outOffering=new JsonObject { ["price"]=money.GetProperty("amount").GetDecimal()/divisor,["quantity"]=offering.GetProperty("quantity").GetInt32(),["is_enabled"]=offering.GetProperty("is_enabled").GetBoolean() };
        if(offering.TryGetProperty("readiness_state_id",out var ready))outOffering["readiness_state_id"]=JsonNode.Parse(ready.GetRawText());
        var props=new JsonArray();
        foreach(var prop in product.GetProperty("property_values").EnumerateArray())
        {
            var item=new JsonObject(); foreach(var name in new[]{"property_id","value_ids","scale_id","property_name","values"})if(prop.TryGetProperty(name,out var value))item[name]=JsonNode.Parse(value.GetRawText()); props.Add(item);
        }
        result["products"]=new JsonArray(new JsonObject { ["sku"]=product.TryGetProperty("sku",out var sku)?JsonNode.Parse(sku.GetRawText()):null,["property_values"]=props,["offerings"]=new JsonArray(outOffering) });
        return result;
    }
    async Task DraftOrContent(EtsyCredentials credentials,EtsyWorkspaceState state,EtsyShopInfo shop,CatalogProduct source,EtsyProductProfile profile,EtsyPreviewRow row,bool create,CancellationToken ct)
    {
        var named=state.Templates.SingleOrDefault(t=>t.Id==profile.TemplateId);
        if(named==null)throw new InvalidOperationException("Ürüne kayıtlı bir Etsy ilan şablonu atayın.");
        var template=JsonSerializer.Deserialize<EtsyListingTemplate>(JsonSerializer.Serialize(named.Listing))!;
        template.TaxonomyId=profile.TaxonomyId??state.CategoryMappings.SingleOrDefault(m=>string.Equals(m.LocalCategory.Trim(),source.Category.Trim(),StringComparison.OrdinalIgnoreCase))?.TaxonomyId??template.TaxonomyId;
        template.ShippingProfileId=profile.ShippingProfileId??template.ShippingProfileId; template.ReadinessStateId=profile.ReadinessStateId??template.ReadinessStateId;
        template.Tags=Text(profile.Tags,template.Tags); template.Materials=Text(profile.Materials,template.Materials);
        var p=JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(source))!;
        p.Name=Text(profile.Title,(template.TitlePrefix.Trim()+" "+p.Name).Trim()); template.TitlePrefix="";
        p.Description=Text(profile.Description,p.Description); p.Price=profile.Price??p.Price; if(profile.Price.HasValue)p.Currency=profile.PriceCurrency; p.EtsyListingId="";
        var errors=EtsyDrafts.Validate(p,template);
        if(!create)errors.RemoveAll(e=>e.Contains("Stok sıfırdan")||e.Contains("Fiyat sıfırdan")||e.Contains("para birimi"));
        if(create&&!SameCurrency(p.Currency,shop.Currency))errors.Add("Ürün ve Etsy mağazası para birimi aynı olmalı.");
        if(errors.Count>0)throw new InvalidOperationException(string.Join(" ",errors));
        var definitions=await new EtsyMetadataClient(http).GetPropertiesAsync(credentials,template.TaxonomyId,ct).ConfigureAwait(false);
        if(profile.Properties.GroupBy(p=>p.PropertyId).Any(g=>g.Count()>1))throw new InvalidOperationException("Yinelenen Etsy özelliği.");
        foreach(var definition in definitions.Where(d=>d.Required&&d.SupportsAttributes))if(!profile.Properties.Any(v=>v.PropertyId==definition.Id&&(v.ValueIds.Length>0||v.Values.Length>0)))throw new InvalidOperationException($"Zorunlu Etsy özelliği eksik: {definition.Name}.");
        foreach(var property in profile.Properties)
        {
            var definition=definitions.SingleOrDefault(d=>d.Id==property.PropertyId);
            if(definition==null||!definition.SupportsAttributes||property.Values.Any(v=>string.IsNullOrWhiteSpace(v)||v.Contains('(')||v.Contains(')'))||property.ValueIds.Any(v=>!definition.Values.Any(d=>d.Id==v))||property.ScaleId.HasValue&&!definition.Scales.Any(s=>s.Id==property.ScaleId))throw new InvalidOperationException("Kategori özelliği veya değeri geçersiz; özellik listesini yenileyin.");
        }
        var path=$"shops/{state.ShopId}/listings"+(create?"":"/"+row.ListingId);
        var fields=new Dictionary<string,string> { ["title"]=p.Name,["description"]=p.Description,["taxonomy_id"]=Number(template.TaxonomyId),["shipping_profile_id"]=Number(template.ShippingProfileId),["who_made"]=template.WhoMade,["when_made"]=template.WhenMade,["is_supply"]=template.IsSupply?"true":"false" };
        if(template.Tags.Length>0)fields["tags"]=template.Tags; if(template.Materials.Length>0)fields["materials"]=template.Materials;
        var section=profile.ShopSectionId??named.ShopSectionId; if(section.HasValue)fields["shop_section_id"]=Number(section.Value);
        if(create) { fields["quantity"]=Number(p.Stock); fields["price"]=Number(p.Price); fields["readiness_state_id"]=Number(template.ReadinessStateId); fields["type"]="physical"; fields["should_auto_renew"]="false"; }
        row.Steps.Add(new() { Method=create?"POST":"PATCH",Path=path,Form=fields });
        if(!create)
        {
            var inventory=await Get(credentials,$"listings/{row.ListingId}/inventory",ct).ConfigureAwait(false);
            var body=SimpleInventory(inventory); row.InventoryFingerprint=InventoryHash(inventory);
            body["products"]![0]!["offerings"]![0]!["readiness_state_id"]=template.ReadinessStateId;
            row.Steps.Add(new() { Method="PUT",Path=$"listings/{row.ListingId}/inventory",Json=body.ToJsonString() });
        }
        if(create)
        {
            // SKU is a separate inventory write, explicitly included in this saved preview.
            var body=new JsonObject { ["products"]=new JsonArray(new JsonObject { ["sku"]=p.Sku,["property_values"]=new JsonArray(),["offerings"]=new JsonArray(new JsonObject { ["price"]=p.Price,["quantity"]=p.Stock,["is_enabled"]=true,["readiness_state_id"]=template.ReadinessStateId }) }),["price_on_property"]=new JsonArray(),["quantity_on_property"]=new JsonArray(),["sku_on_property"]=new JsonArray(),["readiness_state_on_property"]=new JsonArray() };
            row.Steps.Add(new() { Method="PUT",Path="listings/{listing_id}/inventory",Json=body.ToJsonString() });
            var first=p.ImageUrls.Split(['|','\r','\n'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if(first!=null)
            {
                var bytes=await new EtsyDrafts(http).PrepareImageAsync(first,ct).ConfigureAwait(false);
                var prepared=await MarketplaceImages.PrepareAsync(bytes,ImageMarketplace.Etsy).ConfigureAwait(false);
                row.Steps.Add(new() { Method="POST",Path=$"shops/{state.ShopId}/listings/{{listing_id}}/images",Image=prepared.Bytes });
            }
        }
        foreach(var property in profile.Properties)
        {
            var form=new Dictionary<string,string> { ["value_ids"]=string.Join(',',property.ValueIds),["values"]=string.Join(',',property.Values) }; if(property.ScaleId.HasValue)form["scale_id"]=Number(property.ScaleId.Value);
            row.Steps.Add(new() { Method="PUT",Path=$"shops/{state.ShopId}/listings/"+(create?"{listing_id}":row.ListingId)+$"/properties/{property.PropertyId}",Form=form });
        }
        row.Title=p.Name; row.Detail=create?$"Taslak oluştur + SKU ({p.Sku}) + {profile.Properties.Count} özellik; {row.Steps.Count(s=>s.Image!=null)} görsel. Fiyat {p.Price} {shop.Currency}, stok {p.Stock}. Yayına alınmaz.":"Başlık, açıklama, kategori, şablon alanları ve seçilen özellikler güncellenir; fiyat ve stok korunur.";
    }
}
