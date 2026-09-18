using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed partial class TrendyolWorkspaceStore
{
    public IReadOnlyList<TrendyolReceipt> Receipts(string seller)
    {
        Seller(seller);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Json FROM TrendyolReceipts WHERE SellerId=$seller ORDER BY CreatedUtc DESC LIMIT 300";cmd.Parameters.AddWithValue("$seller",seller);using var r=cmd.ExecuteReader();var result=new List<TrendyolReceipt>();while(r.Read()){var receipt=JsonSerializer.Deserialize<TrendyolReceipt>(r.GetString(0))??throw new InvalidDataException("Gönderim geçmişi okunamadı.");if(receipt.SellerId!=seller)throw new InvalidDataException("Gönderim hesabı tutarsız.");result.Add(receipt);}return result;
    }
    public TrendyolPlan Claim(string id,TrendyolSettings account,bool explicitlyApproved)
    {
        if(!explicitlyApproved)throw new InvalidOperationException("Önizlemenin açıkça onaylanması gerekli.");
        TrendyolConnection.Validate(account);using var c=Open();using var tx=c.BeginTransaction();
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Json FROM TrendyolPlans WHERE Id=$id AND SellerId=$seller";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$seller",account.SupplierId);
        var plan=JsonSerializer.Deserialize<TrendyolPlan>(cmd.ExecuteScalar() as string??throw new InvalidOperationException("Önizleme bu mağazada bulunamadı."))!;
        if(plan.Id!=id||plan.SellerId!=account.SupplierId||plan.AccountFingerprint!=AccountFingerprint(account))throw new InvalidOperationException("Mağaza veya erişim bilgileri değişti; yeniden önizleyin.");
        if(DateTime.UtcNow-plan.CreatedUtc>TimeSpan.FromMinutes(15)||plan.CreatedUtc>DateTime.UtcNow||Load(c,tx,account.SupplierId).Revision!=plan.Revision||CatalogFingerprint(c,tx,plan.Rows.Select(r=>r.ProductId))!=plan.CatalogFingerprint)throw new InvalidOperationException("Önizleme eskidi; katalog veya ayarlar değişti. Yeniden önizleyin.");
        if(!plan.Rows.Any(r=>r.ItemJson!=null)||plan.Rows.Any(r=>r.Status=="Hatalı"))throw new InvalidOperationException("Hatalı satırları düzeltin veya seçimden çıkarıp yeniden önizleyin.");
        var payload=JsonSerializer.Serialize(new{items=plan.Rows.Where(r=>r.ItemJson!=null).Select(r=>JsonSerializer.Deserialize<JsonElement>(r.ItemJson!)).ToArray()});
        if(payload!=plan.PayloadJson||!Enum.IsDefined(plan.Operation))throw new InvalidDataException("Önizleme içeriği tutarsız.");
        var hash=Hash(plan.Operation+"|"+payload);
        using(var check=c.CreateCommand())
        {
            check.Transaction=tx;check.CommandText="SELECT PlanId,PayloadHash,CreatedUtc,Json FROM TrendyolReceipts WHERE SellerId=$seller";check.Parameters.AddWithValue("$seller",plan.SellerId);using var r=check.ExecuteReader();
            while(r.Read())
            {
                var previous=JsonSerializer.Deserialize<TrendyolReceipt>(r.GetString(3))??throw new InvalidDataException("Gönderim geçmişi okunamadı.");
                if(r.GetString(0)==id)throw new InvalidOperationException("Bu önizleme daha önce gönderildi; işlem geçmişini kontrol edin.");
                if(previous.Status is "Sonuç bekleniyor" or "Belirsiz")throw new InvalidOperationException("Önceki gönderimin sonucu belirsiz. İşlem geçmişinden mağazayı kontrol ederek sonuçlandırın; otomatik tekrar yapılmaz.");
                if(r.GetString(1)==hash)
                {
                    var retryCreate=plan.Operation==TrendyolOperation.Create && previous.Status is "Hatalı" or "Elle kontrol edildi";
                    if(retryCreate && Load(c,tx,account.SupplierId).ProductsUpdatedUtc<=previous.CreatedUtc)throw new InvalidOperationException("Sonuç kontrolünden sonra mağaza ürünlerini yeniden alın.");
                    if(!retryCreate && (plan.Operation==TrendyolOperation.Create || DateTime.UtcNow-previous.CreatedUtc<TimeSpan.FromMinutes(15)))throw new InvalidOperationException("Aynı içerik daha önce gönderildi; mağaza verisini ve sonucu kontrol edin.");
                }
            }
        }
        var receipt=new TrendyolReceipt(id,plan.SellerId,DateTime.UtcNow,plan.Operation.ToString(),"Sonuç bekleniyor","","İstek hazırlanıyor; tekrar göndermeyin.");
        using var save=c.CreateCommand();save.Transaction=tx;save.CommandText="INSERT INTO TrendyolReceipts VALUES($id,$seller,$hash,$time,$json)";save.Parameters.AddWithValue("$id",id);save.Parameters.AddWithValue("$seller",plan.SellerId);save.Parameters.AddWithValue("$hash",hash);save.Parameters.AddWithValue("$time",receipt.CreatedUtc.ToString("O"));save.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt));save.ExecuteNonQuery();tx.Commit();return plan;
    }
    public async Task<TrendyolReceipt> SendAsync(string planId,TrendyolSettings account,bool approved,TrendyolApiClient client,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(client.AccountFingerprint!=AccountFingerprint(account))throw new InvalidOperationException("API istemcisi farklı hesaba ait; gönderilmedi.");
        var plan=Claim(planId,account,approved);
        try
        {
            var operation=plan.Operation switch{TrendyolOperation.Create=>"create",TrendyolOperation.UpdateUnapproved=>"unapproved",TrendyolOperation.Content=>"content",TrendyolOperation.Delivery=>"delivery",TrendyolOperation.ShippingDetails=>"details",_=>"inventory"};
            var batch=await client.SendBatchAsync(operation,plan.PayloadJson,cancellationToken);
            return UpdateReceipt(planId,account.SupplierId,"Kuyrukta",batch,"İstek kabul edildi; ürünün yayımlandığı anlamına gelmez. Sonucu sorgulayın.");
        }
        catch { UpdateReceipt(planId,account.SupplierId,"Belirsiz","","Gönderimin sonucu doğrulanamadı. Mağazada kontrol etmeden tekrar göndermeyin.");throw new InvalidOperationException("Gönderim sonucu belirsiz. Ayrıntı için İşlem geçmişi sekmesini açın."); }
    }
    public TrendyolReceipt UpdateBatch(string planId,string seller,JsonElement response)
    {
        var previous=Receipts(seller).Single(r=>r.PlanId==planId);
        if(previous.BatchId.Length==0||!response.TryGetProperty("batchRequestId",out var id)||id.GetString()!=previous.BatchId||!response.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength()==0)throw new InvalidOperationException("Toplu işlem yanıtı eksik veya farklı işleme ait.");
        using var c=Open();using var read=c.CreateCommand();read.CommandText="SELECT Json FROM TrendyolPlans WHERE Id=$id AND SellerId=$seller";read.Parameters.AddWithValue("$id",planId);read.Parameters.AddWithValue("$seller",seller);
        var plan=JsonSerializer.Deserialize<TrendyolPlan>(read.ExecuteScalar() as string??throw new InvalidOperationException("Gönderimin önizlemesi bulunamadı."))!;
        var expected=plan.Rows.Where(r=>r.ItemJson!=null).Select(r=>JsonSerializer.Deserialize<JsonElement>(r.ItemJson!)).Select(item=>plan.Operation==TrendyolOperation.Content?item.GetProperty("contentId").ToString():item.GetProperty("barcode").GetString()!).ToHashSet(StringComparer.Ordinal);
        var returned=new HashSet<string>(StringComparer.Ordinal);
        var detail=new List<string>();var failed=0;var pending=0;
        foreach(var item in items.EnumerateArray())
        {
            var status=item.TryGetProperty("status",out var s)?s.GetString():null;
            if(status=="FAILED")failed++;else if(status!="SUCCESS")pending++;
            if(!item.TryGetProperty("requestItem",out var req))throw new InvalidOperationException("Toplu işlem yanıtında ürün kimliği yok.");
            if(plan.Operation is TrendyolOperation.Content or TrendyolOperation.ShippingDetails && req.TryGetProperty("updateRequest",out var nested))req=nested;
            if(!req.TryGetProperty(plan.Operation==TrendyolOperation.Content?"contentId":"barcode",out var identifier))throw new InvalidOperationException("Toplu işlem yanıtında ürün kimliği yok.");
            var barcode=identifier.ToString();if(!expected.Contains(barcode)||!returned.Add(barcode))throw new InvalidOperationException("Toplu işlem yanıtı farklı veya yinelenen ürün içeriyor.");
            var reasons=item.TryGetProperty("failureReasons",out var reasonsNode)&&reasonsNode.ValueKind==JsonValueKind.Array?string.Join("; ",reasonsNode.EnumerateArray().Select(x=>x.ToString())):"";
            if(detail.Count<100)detail.Add($"{barcode}: {status} {reasons}");
        }
        if(response.TryGetProperty("status",out var top)&&top.GetString()!="COMPLETED")pending++;
        if(response.TryGetProperty("itemCount",out var count)&&count.GetInt32()>items.GetArrayLength())pending++;
        if(!returned.SetEquals(expected))pending++;
        return UpdateReceipt(planId,seller,pending>0?"İşleniyor":failed>0?"Hatalı":"İşlem tamamlandı",previous.BatchId,MarketplaceConnectionStore.Redact(string.Join("\n",detail)));
    }
    public void ResolveUnknown(string planId,string seller,string note)
    {
        var previous=Receipts(seller).Single(r=>r.PlanId==planId);
        if(previous.Status is not ("Belirsiz" or "Sonuç bekleniyor")||note.Trim().Length<10)throw new InvalidOperationException("Mağazada yaptığınız kontrolü en az 10 karakter ile yazın.");
        UpdateReceipt(planId,seller,"Elle kontrol edildi",previous.BatchId,"Kullanıcı mağazada kontrol etti: "+note.Trim());
    }
    TrendyolReceipt UpdateReceipt(string id,string seller,string status,string batch,string detail)
    {
        using var c=Open();using var tx=c.BeginTransaction();using var read=c.CreateCommand();read.Transaction=tx;read.CommandText="SELECT Json FROM TrendyolReceipts WHERE PlanId=$id AND SellerId=$seller";read.Parameters.AddWithValue("$id",id);read.Parameters.AddWithValue("$seller",seller);var previous=JsonSerializer.Deserialize<TrendyolReceipt>(read.ExecuteScalar() as string??throw new InvalidOperationException("Gönderim kaydı yok."))!;
        var next=previous with{Status=status,BatchId=batch,Detail=detail};using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE TrendyolReceipts SET Json=$json WHERE PlanId=$id AND SellerId=$seller";cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(next));cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$seller",seller);cmd.ExecuteNonQuery();tx.Commit();return next;
    }
}
