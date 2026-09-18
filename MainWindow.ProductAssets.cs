using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow
{
 bool assetSyncRunning;bool assetSyncAgain;
 async void QueueProductAssets()
 {
  if(assetSyncRunning){assetSyncAgain=true;return;}assetSyncRunning=true;
  try
  {
   do
   {
    assetSyncAgain=false;
    var result=await Task.Run(async()=>
    {
     var cache=new ProductAssetCache(dataDirectory);cache.CleanupDeletedProducts();using var client=SafeRemoteHttp.CreateClient(TimeSpan.FromSeconds(15));var saved=0;var failed=0;
     foreach(var product in store.Products())
      foreach(var url in ProductAssetCache.Urls(product))
      {
       if(lifetime.IsCancellationRequested)return(saved,failed,cache.PendingCleanupCount());
       try{if(cache.Find(product.Id,url)==null){await cache.EnsureAsync(product,url,client,lifetime.Token);saved++;}}
       catch(OperationCanceledException){return(saved,failed,cache.PendingCleanupCount());}
       catch{failed++;}
      }
     return(saved,failed,cache.PendingCleanupCount());
    });
    if(!lifetime.IsCancellationRequested && (result.saved>0||result.failed>0||result.Item3>0))Log($"Görsel arşivi: {result.saved} kopya kaydedildi, {result.failed} bağlantı alınamadı, {result.Item3} temizlik bekliyor.");
   }while(assetSyncAgain&&!lifetime.IsCancellationRequested);
  }
  catch(Exception ex){if(!lifetime.IsCancellationRequested)Log("Görsel arşivi: "+Safe(ex));}
  finally{assetSyncRunning=false;}
 }
}
