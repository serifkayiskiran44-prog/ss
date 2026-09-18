using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;
public sealed record TrendyolProductMatchChoice(string ProductId,string CatalogHash,string Barcode,bool CreateIfMissing=false);
public sealed partial class TrendyolWorkspaceStore
{
    public void ApplyProductMatches(string seller,long expectedRevision,IReadOnlyList<TrendyolProductMatchChoice> choices)
    {
        Seller(seller);var selected=choices.ToArray();if(selected.Length==0)throw new InvalidOperationException("Kaydedilecek eşleşme yok.");
        if(selected.GroupBy(c=>c.ProductId).Any(g=>g.Count()>1)||selected.GroupBy(c=>c.Barcode).Any(g=>g.Count()>1))throw new InvalidOperationException("Aynı ürün veya Trendyol barkodu birden fazla satıra atanamaz.");
        using var connection=Open();using var transaction=connection.BeginTransaction();var next=Load(connection,transaction,seller);
        if(next.Revision!=expectedRevision)throw new InvalidOperationException("Eşleştirme listesi eskidi; yeniden eşleştirme önizlemesi açın.");
        if(selected.Any(c=>c.CreateIfMissing))Fresh(next.ProductsUpdatedUtc,TimeSpan.FromHours(24),"Yeni ürün kararı için mağaza ürünlerini yenileyin");
        foreach(var choice in selected)
        {
            if(choice.Barcode.Length is <1 or >40||choice.Barcode.Any(ch=>!char.IsLetterOrDigit(ch)&&ch!='.'&&ch!='-'&&ch!='_'))throw new InvalidOperationException("Geçerli bir Trendyol barkodu girin (1–40 karakter, boşluksuz).");
            var exists=next.Products.Any(p=>p.Barcode==choice.Barcode);
            if(choice.CreateIfMissing&&exists)throw new InvalidOperationException("Bu barkod mağazada var; yeni ürün oluşturulamaz.");
            if(!choice.CreateIfMissing&&!exists)throw new InvalidOperationException("Seçilen ürün bu mağazanın listesinde yok.");
            using var read=connection.CreateCommand();read.Transaction=transaction;read.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";read.Parameters.AddWithValue("$id",choice.ProductId);
            var product=JsonSerializer.Deserialize<CatalogProduct>(read.ExecuteScalar() as string??throw new InvalidOperationException("Yerel ürün silindi; eşleştirmeyi yenileyin."))!;
            if(Hash(JsonSerializer.Serialize(product))!=choice.CatalogHash)throw new InvalidOperationException("Yerel ürün değişti; eşleştirmeyi yeniden inceleyin.");
            if(next.Profiles.Any(p=>p.ProductId!=choice.ProductId&&p.IntegrationCode==choice.Barcode))throw new InvalidOperationException("Trendyol barkodu başka bir yerel ürüne bağlı.");
            var profile=next.Profiles.SingleOrDefault(p=>p.ProductId==choice.ProductId);if(profile is null){profile=new(){ProductId=choice.ProductId};next.Profiles.Add(profile);}profile.IntegrationCode=choice.Barcode;profile.ListingBarcode=choice.Barcode;
        }
        Validate(next);next.Revision++;using var write=connection.CreateCommand();write.Transaction=transaction;
        write.CommandText="UPDATE TrendyolWorkspace SET Revision=$revision,Json=$json WHERE SellerId=$seller";write.Parameters.AddWithValue("$revision",next.Revision);write.Parameters.AddWithValue("$json",JsonSerializer.Serialize(next));write.Parameters.AddWithValue("$seller",seller);
        if(write.ExecuteNonQuery()!=1)throw new InvalidOperationException("Mağaza kaydı bulunamadı.");transaction.Commit();
    }
}
