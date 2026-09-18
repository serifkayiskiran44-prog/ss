using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed class TrendyolBrandSafetyTemplate
{
    public string BrandName { get; set; } = "";
    public Dictionary<long,string> Values { get; set; } = [];
}
public sealed record TrendyolSafetyField(long Id,string Name);
public static class TrendyolProductSafety
{
    // Verified against category metadata. Each outgoing category is checked again in the preview.
    public static IReadOnlyList<TrendyolSafetyField> Fields { get; } = new TrendyolSafetyField[]
    {
        new(1198,"Üretici Adı"),new(1294,"Üretici Mail Adresi"),new(1296,"Üretici Adres Bilgisi"),
        new(1216,"Birincil İthalatçı Adı"),new(1305,"Birincil İthalatçı Mail Adresi"),new(1304,"Birincil İthalatçı Adres Bilgisi"),
        new(1297,"İkincil İthalatçı Adı"),new(1298,"İkincil İthalatçı Mail Adresi"),new(1299,"İkincil İthalatçı Adres Bilgisi"),
        new(1300,"Üçüncül İthalatçı Adı"),new(1301,"Üçüncül İthalatçı Mail Adresi"),new(1302,"Üçüncül İthalatçı Adres Bilgisi"),
        new(1116,"Kullanım Talimatı/Uyarıları")
    };
    public static void Validate(TrendyolBrandSafetyTemplate template)
    {
        if(string.IsNullOrWhiteSpace(template.BrandName)||template.BrandName.Length>200||template.Values is null)
            throw new InvalidOperationException("Marka denetim şablonunu kontrol edin.");
        foreach(var pair in template.Values)
        {
            var field=Fields.SingleOrDefault(f=>f.Id==pair.Key)??throw new InvalidOperationException("Bilinmeyen ürün denetim alanı.");
            if(pair.Value is null||pair.Value.Length>4000)throw new InvalidOperationException(field.Name+": yerel şablon en fazla 4000 karakter saklayabilir.");
            if(field.Name.Contains("Mail")&&!string.IsNullOrWhiteSpace(pair.Value)&&(!System.Net.Mail.MailAddress.TryCreate(pair.Value,out var mail)||mail.Address!=pair.Value))
                throw new InvalidOperationException(field.Name+": geçerli e-posta adresi girin.");
        }
    }
    public static List<TrendyolAttributeSelection> Resolve(TrendyolWorkspaceState state,CatalogProduct product,TrendyolProductProfile profile,IReadOnlyList<TrendyolAttribute> definitions)
    {
        var result=profile.Attributes.Where(a=>a.ValueIds.Length>0||a.CustomValue.Length>0).ToList();
        var template=state.BrandSafetyTemplates.SingleOrDefault(t=>TrendyolMatching.Normalize(t.BrandName)==TrendyolMatching.Normalize(product.Brand));
        if(template is null)return result;
        foreach(var pair in template.Values.Where(p=>!string.IsNullOrWhiteSpace(p.Value)))
        {
            if(result.Any(a=>a.AttributeId==pair.Key))continue;
            var field=Fields.Single(f=>f.Id==pair.Key);
            if(!definitions.Any(d=>d.Id==pair.Key&&d.AllowCustom))throw new InvalidOperationException("Marka şablonundaki '"+field.Name+"' bu kategoride serbest metin olarak desteklenmiyor; kategori özelliklerini yenileyin veya şablonu kontrol edin.");
            result.Add(new(pair.Key,[],pair.Value));
        }
        return result;
    }
}

public sealed partial class TrendyolWorkspaceStore
{
    public void ClearBrandSafetyFields(string sellerId,long expectedRevision,IEnumerable<string> brandNames,IEnumerable<long> fieldIds)
    {
        var next=Load(sellerId);if(next.Revision!=expectedRevision)throw new InvalidOperationException("Marka şablonları değişti; ekranı yenileyin.");
        var names=brandNames.Select(TrendyolMatching.Normalize).ToHashSet(StringComparer.Ordinal);
        var ids=fieldIds.Distinct().ToArray();
        if(names.Count is <1 or >1000||names.Contains("")||ids.Length==0||ids.Any(id=>!TrendyolProductSafety.Fields.Any(f=>f.Id==id)))
            throw new InvalidOperationException("Kaldırılacak alanı ve markaları seçin.");
        foreach(var template in next.BrandSafetyTemplates.Where(t=>names.Contains(TrendyolMatching.Normalize(t.BrandName))))
            foreach(var id in ids)template.Values.Remove(id);
        Save(next);
    }
    public void SaveBrandSafety(string sellerId,long expectedRevision,IEnumerable<string> brandNames,Dictionary<long,string> values)
    {
        var next=Load(sellerId);if(next.Revision!=expectedRevision)throw new InvalidOperationException("Marka şablonları değişti; ekranı yenileyin.");
        var names=brandNames.Select(n=>n.Trim()).DistinctBy(TrendyolMatching.Normalize).ToArray();
        if(names.Length is <1 or >1000||names.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("1–1000 marka seçin.");
        var filled=values.Where(v=>!string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v=>v.Key,v=>v.Value.Trim());
        if(filled.Count==0)throw new InvalidOperationException("Kaydedilecek en az bir denetim alanını doldurun. Boş alanlar mevcut değeri korur.");
        foreach(var name in names)
        {
            var template=next.BrandSafetyTemplates.SingleOrDefault(t=>TrendyolMatching.Normalize(t.BrandName)==TrendyolMatching.Normalize(name));
            if(template is null){template=new(){BrandName=name};next.BrandSafetyTemplates.Add(template);}
            foreach(var pair in filled)template.Values[pair.Key]=pair.Value;
            TrendyolProductSafety.Validate(template);
        }
        Save(next);
    }
}
