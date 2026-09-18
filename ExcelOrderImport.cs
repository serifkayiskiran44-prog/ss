using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public static class ExcelOrderImport
{
    public static IReadOnlyList<ExcelProductField> Fields{get;}=new List<ExcelProductField>
    {
        new("OrderId","Sipariş numarası","Genel"),new("CustomerName","Müşteri adı soyadı","Genel"),new("PaymentStatus","Ödeme durumu / tipi","Genel"),new("RawStatus","Sipariş durumu","Genel"),new("Currency","Para birimi","Genel"),new("Total","Sipariş toplamı","Genel"),
        new("BillingName","Fatura adı soyadı","Fatura"),new("BillingAddress","Fatura adresi","Fatura"),new("BillingCity","Fatura ili","Fatura"),new("BillingDistrict","Fatura ilçesi","Fatura"),new("TaxNumber","Vergi / kimlik numarası","Fatura"),
        new("ShippingName","Alıcı adı soyadı","Sevk"),new("ShippingAddress","Sevk adresi","Sevk"),new("ShippingCity","Sevk ili","Sevk"),new("ShippingDistrict","Sevk ilçesi","Sevk"),
        new("Sku","Ürün stok kodu / SKU","Ürünler"),new("Name","Ürün adı","Ürünler"),new("Quantity","Adet","Ürünler"),new("UnitPrice","Birim fiyat","Ürünler"),new("VatRate","KDV oranı (%)","Ürünler")
    }.AsReadOnly();
    internal static string Scope(string market,string shop)=>JsonSerializer.Serialize(new[]{market.Trim(),shop.Trim()});
    static void ValidateScope(string market,string shop){if(string.IsNullOrWhiteSpace(market)||string.IsNullOrWhiteSpace(shop)||market.Length>80||shop.Length>200)throw new InvalidOperationException("Pazaryeri ve mağaza bilgisi zorunlu.");}
    public static ExcelAuxiliaryPlan Preview(OrdersStore store,string path,ExcelImportProfile profile,string marketplace,string shopId)
    {
        ValidateScope(marketplace,shopId);marketplace=marketplace.Trim();shopId=shopId.Trim();
        using var data=new ExcelWorkbookData(path,profile);
        bool Mapped(string key)=>data.Mapped(key) && (key is "Sku" or "OrderId" || profile.SelectedFields is null || profile.SelectedFields.Contains(key,StringComparer.OrdinalIgnoreCase));
        foreach(var field in new[]{"OrderId","Sku"})if(!Mapped(field))throw new InvalidOperationException(field+" sütunu zorunlu.");
        if(data.Columns.Keys.Any(key=>!Fields.Any(f=>f.Key.Equals(key,StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("Desteklenmeyen sipariş alanı.");
        var snapshot=store.ExcelOrderSnapshot();var previous=JsonSerializer.Deserialize<List<OrderSnapshot>>(snapshot.Json)!;
        var existing=previous.Where(o=>o.Marketplace==marketplace && o.ShopId==shopId).ToDictionary(o=>o.OrderId,StringComparer.Ordinal);
        var groups=new Dictionary<string,OrderSnapshot>();var numbers=new Dictionary<string,List<int>>();var seen=new HashSet<string>();var rows=new List<ExcelAuxiliaryRow>();var errors=new HashSet<string>();var explicitTotals=new Dictionary<string,decimal>();var scalarValues=new Dictionary<string,string>();
        foreach(var row in data.Rows)
        {
            var id="";try
            {
                id=data.Text(row,"OrderId");var sku=data.Text(row,"Sku");if(id.Length==0||id.Length>200||sku.Length==0||sku.Length>128)throw new InvalidOperationException("Geçerli sipariş numarası ve SKU zorunlu.");
                if(!seen.Add(JsonSerializer.Serialize(new[]{id,CatalogStore.NormalizeIdentityKey(sku)})))throw new InvalidOperationException("Aynı siparişte SKU yineleniyor.");
                if(!groups.TryGetValue(id,out var order)){order=existing.GetValueOrDefault(id)?.Copy()??new OrderSnapshot{Marketplace=marketplace,ShopId=shopId,OrderId=id,Currency="TRY",Source="Excel"};groups[id]=order;numbers[id]=new();}
                numbers[id].Add(row.RowNumber());
                var item=order.Items.SingleOrDefault(i=>CatalogStore.NormalizeIdentityKey(i.Sku)==CatalogStore.NormalizeIdentityKey(sku));
                if(item==null){if(!Mapped("Quantity")||data.Text(row,"Quantity").Length==0)throw new InvalidOperationException("Yeni sipariş ürünü için adet zorunlu.");item=new OrderItem{Sku=sku};order.Items.Add(item);}
                foreach(var field in Fields.Where(f=>typeof(OrderSnapshot).GetProperty(f.Key)?.PropertyType==typeof(string) && f.Key!="OrderId" && Mapped(f.Key)))
                {
                    var value=data.Text(row,field.Key);if(value.Length==0)continue;if(value.Length>(field.Key.Contains("Address")?2000:300))throw new InvalidOperationException(field.Label+": metin çok uzun.");
                    var key=JsonSerializer.Serialize(new[]{id,field.Key});if(scalarValues.TryGetValue(key,out var prior)&&prior!=value)throw new InvalidOperationException("Aynı siparişin satırlarında "+field.Label+" farklı.");scalarValues[key]=value;
                    if(field.Key=="Currency"){value=value.ToUpperInvariant();if(value=="TL")value="TRY";if(!LocaleSettings.SupportedCurrencies.Contains(value))throw new InvalidOperationException("Para birimi desteklenmiyor.");}
                    typeof(OrderSnapshot).GetProperty(field.Key)!.SetValue(order,value);
                }
                if(Mapped("Name")&&data.Text(row,"Name").Length>0)item.Title=data.Text(row,"Name");
                if(Mapped("Quantity")&&data.Text(row,"Quantity").Length>0){var quantity=data.Number(row,"Quantity");if(quantity<=0||quantity>int.MaxValue||decimal.Truncate(quantity)!=quantity)throw new InvalidOperationException("Sipariş adedi pozitif tam sayı olmalı.");item.Quantity=(int)quantity;}
                if(Mapped("VatRate")&&data.Text(row,"VatRate").Length>0)item.VatRate=data.Number(row,"VatRate");if(item.VatRate is <0 or >100)throw new InvalidOperationException("KDV oranı 0–100 arasında olmalı.");
                if(Mapped("UnitPrice")&&data.Text(row,"UnitPrice").Length>0){var price=data.Number(row,"UnitPrice");if(price<0)throw new InvalidOperationException("Fiyat negatif olamaz.");item.UnitPrice=Math.Round(profile.PriceIncludesVat?price:checked(price*(1+item.VatRate/100)),2,MidpointRounding.AwayFromZero);}
                if(Mapped("Total")&&data.Text(row,"Total").Length>0){var total=data.Number(row,"Total");if(total<0)throw new InvalidOperationException("Toplam negatif olamaz.");if(explicitTotals.TryGetValue(id,out var oldTotal)&&oldTotal!=total)throw new InvalidOperationException("Aynı siparişin satırlarında toplam tutar farklı.");explicitTotals[id]=total;}
                OrdersRules.Validate(order);rows.Add(new(row.RowNumber(),existing.ContainsKey(id)?"UPDATE":"CREATE",id,item.Title,$"SKU {sku} · {item.Quantity} adet · {item.UnitPrice:0.00} {order.Currency}"));
            }
            catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or OverflowException or FormatException){errors.Add(id);rows.Add(new(row.RowNumber(),"ERROR",id,"",ex.Message));}
        }
        var changes=new List<ExcelAuxiliaryChange>();
        foreach(var (id,order) in groups)
        {
            if(errors.Contains(id))continue;
            try
            {
                if(explicitTotals.TryGetValue(id,out var total))
                {
                    if(!profile.PriceIncludesVat)
                    {
                        var rates=order.Items.Select(i=>i.VatRate).Distinct().ToArray();
                        if(rates.Length==1)total=Math.Round(checked(total*(1+rates[0]/100)),2,MidpointRounding.AwayFromZero);
                        else
                        {
                            if(order.Items.Any(i=>!i.UnitPrice.HasValue))throw new InvalidOperationException("Karma KDV toplamı için tüm birim fiyatlarını eşleyin.");
                            var net=order.Items.Sum(i=>Math.Round(i.UnitPrice!.Value/(1+i.VatRate/100),2,MidpointRounding.AwayFromZero)*i.Quantity);
                            if(net!=total)throw new InvalidOperationException("Karma KDV toplamı ürünlerin net toplamıyla uyuşmuyor. İndirim/kargo içeren toplamı KDV dâhil aktarın.");
                            total=order.Items.Sum(i=>checked(i.UnitPrice!.Value*i.Quantity));
                        }
                    }
                    order.Total=total;
                }
                else if(!existing.ContainsKey(id) && order.Items.All(i=>i.UnitPrice.HasValue))order.Total=order.Items.Sum(i=>checked(i.UnitPrice!.Value*i.Quantity));
                else if(!existing.ContainsKey(id))throw new InvalidOperationException("Yeni sipariş için tüm birim fiyatları veya sipariş toplamını eşleyin.");
                var old=existing.GetValueOrDefault(id);var unchanged=old!=null&&JsonSerializer.Serialize(old)==JsonSerializer.Serialize(order);
                if(unchanged){for(var i=0;i<rows.Count;i++)if(rows[i].Key==id)rows[i]=rows[i] with {Action="SKIP",Note="Seçili alanlarda değişiklik yok."};continue;}
                order.Source="Excel";order.UpdatedAt=DateTimeOffset.UtcNow;order.SourceUpdatedAt=order.UpdatedAt;
                changes.Add(new(id,JsonSerializer.Serialize(order),numbers[id].AsReadOnly()));
            }
            catch(Exception ex)when(ex is InvalidOperationException or OverflowException){for(var i=0;i<rows.Count;i++)if(rows[i].Key==id)rows[i]=rows[i] with {Action="ERROR",Note=ex.Message};}
        }
        return new(rows,changes,data.FileHash,ExcelWorkbookData.Signature(profile),snapshot.Identity,snapshot.Json,Scope(marketplace,shopId));
    }
    public static string Apply(OrdersStore store,string path,ExcelImportProfile profile,ExcelAuxiliaryPlan? plan,IReadOnlyCollection<int> selected,string marketplace,string shopId)
    {
        if(plan is null)throw new InvalidOperationException("Önce sipariş önizlemesi oluşturun.");ValidateScope(marketplace,shopId);plan.Validate(path,profile,selected,Scope(marketplace,shopId));
        if(plan.Changes.Any(c=>c.RowNumbers.Any(selected.Contains)&&!c.RowNumbers.All(selected.Contains)))throw new InvalidOperationException("Bir siparişin tüm satırlarını birlikte seçin.");
        return store.ApplyExcelOrders(plan,selected);
    }
}
public sealed partial class OrdersStore
{
    static string ExcelOrdersJson(SqliteConnection c,SqliteTransaction tx)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT marketplace,shop,id,payload FROM orders ORDER BY marketplace,shop,id";using var reader=cmd.ExecuteReader();var orders=new List<OrderSnapshot>();
        while(reader.Read()){if(!TryReadOrder(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),out var order,out _))throw new InvalidOperationException("Sipariş kaydı bozuk; Excel işlemi durduruldu.");orders.Add(order!);}return JsonSerializer.Serialize(orders);
    }
    internal (string Json,string Identity) ExcelOrderSnapshot(){using var c=Open();using var tx=c.BeginTransaction(deferred:true);return(ExcelOrdersJson(c,tx),Path.GetFullPath(c.DataSource));}
    internal string ApplyExcelOrders(ExcelAuxiliaryPlan plan,IReadOnlyCollection<int> selected)
    {
        using var c=Open();using var tx=c.BeginTransaction();var before=ExcelOrdersJson(c,tx);
        if(!Path.GetFullPath(c.DataSource).Equals(plan.StoreIdentity,StringComparison.OrdinalIgnoreCase)||before!=plan.Snapshot)throw new InvalidOperationException("Siparişler önizlemeden sonra değişti veya farklı veri klasörü seçildi.");
        foreach(var change in plan.Changes.Where(p=>p.RowNumbers.All(selected.Contains)))PutExcelOrder(c,tx,JsonSerializer.Deserialize<OrderSnapshot>(change.Json)!);
        ExcelJournal.Save(c,tx,plan.Id,"orders",before,ExcelOrdersJson(c,tx));tx.Commit();return plan.Id;
    }
    static void PutExcelOrder(SqliteConnection c,SqliteTransaction tx,OrderSnapshot order)
    {
        OrdersRules.Validate(order);using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO orders(marketplace,shop,id,payload) VALUES($m,$s,$i,$p) ON CONFLICT(marketplace,shop,id) DO UPDATE SET payload=excluded.payload";Key(cmd,order);cmd.Parameters.AddWithValue("$p",JsonSerializer.Serialize(order));cmd.ExecuteNonQuery();
    }
    public string? LastExcelOrderImportId(){using var c=Open();return ExcelJournal.Last(c,"orders");}
    public void UndoExcelOrders(string id)
    {
        using var c=Open();using var tx=c.BeginTransaction();var receipt=ExcelJournal.Read(c,tx,id,"orders");if(ExcelOrdersJson(c,tx)!=receipt.After)throw new InvalidOperationException("Siparişler işlemden sonra değişti; geri alma durduruldu.");
        string Identity(OrderSnapshot o)=>JsonSerializer.Serialize(new[]{o.Marketplace,o.ShopId,o.OrderId});
        var before=JsonSerializer.Deserialize<List<OrderSnapshot>>(receipt.Before)!.ToDictionary(Identity);
        foreach(var order in JsonSerializer.Deserialize<List<OrderSnapshot>>(receipt.After)!)
        {
            if(before.TryGetValue(Identity(order),out var old)){if(JsonSerializer.Serialize(order)!=JsonSerializer.Serialize(old))PutExcelOrder(c,tx,old);}
            else{using var del=c.CreateCommand();del.Transaction=tx;del.CommandText="DELETE FROM orders WHERE marketplace=$m AND shop=$s AND id=$i";Key(del,order);del.ExecuteNonQuery();}
        }
        ExcelJournal.Complete(c,tx,id);tx.Commit();
    }
}
