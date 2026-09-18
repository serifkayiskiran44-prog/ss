using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;
public sealed record TrendyolCompetitionRow(string ProductId,string Sku,string Barcode,int? Rank,decimal? First,decimal? Second,decimal? Third,decimal? Suggested,string Reason,decimal Minimum,decimal Maximum,decimal Difference);
public sealed partial class TrendyolWorkspacePanel
{
    readonly DataGrid competition=Grid("TrendyolCompetition",("SKU","Sku",100),("Barkod","Barcode",150),("Sıra","Rank",60),("1. fiyat","First",100),("2. fiyat","Second",100),("3. fiyat","Third",100),("Öneri (TRY)","Suggested",120),("Açıklama","Reason",440));
    readonly TextBox minimum=Box(),maximum=Box(),difference=Box();
    readonly DataGrid history=Grid("TrendyolHistory",("Zaman (UTC)","CreatedUtc",160),("İşlem","Operation",130),("Durum","Status",170),("Batch ID","BatchId",300));
    readonly TextBox historyDetail=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new(5)};
    readonly TextBox resolutionNote=Box();
    long competitionRevision;
    string competitionAccount="";
    FrameworkElement BuildCompetition()
    {
        var dock=new DockPanel();var top=new StackPanel();top.Children.Add(T("Trendyol kontrol ekranında en fazla 50 eşleşmiş ürün seçin. Buybox sırası ve ilk üç fiyat burada gösterilir.",true));top.Children.Add(B("← Kontrol listesinde ürün seç",()=>{tabs.SelectedIndex=0;ShowProductView("list");}));
        foreach(var box in new[]{minimum,maximum,difference})box.TextChanged+=(_,_)=>{competition.ItemsSource=null;competitionAccount="";};
        var actions=new WrapPanel();actions.Children.Add(T("Minimum TRY"));minimum.Text="";actions.Children.Add(minimum);actions.Children.Add(T("Maksimum TRY"));actions.Children.Add(maximum);actions.Children.Add(T("Fark TRY"));difference.Text="0,01";actions.Children.Add(difference);
        actions.Children.Add(A("Buybox bilgilerini al ve öner",async()=>{
            var ids=SelectedProductIds();if(ids.Length>50)throw new InvalidOperationException("Bir seferde en fazla 50 ürün seçin.");var account=Account();var current=store.Load(account.SupplierId);competitionRevision=current.Revision;competitionAccount=TrendyolWorkspaceStore.AccountFingerprint(account);
            var profiles=ids.Select(id=>current.Profiles.SingleOrDefault(p=>p.ProductId==id)??throw new InvalidOperationException("Önce ürünleri eşleştirin.")).ToArray();if(profiles.Any(p=>p.IntegrationCode.Length==0))throw new InvalidOperationException("Önce ürünleri eşleştirin.");
            var min=OptionalDecimal(minimum.Text);var max=OptionalDecimal(maximum.Text);var diff=OptionalDecimal(difference.Text);using var client=new TrendyolApiClient(account);var results=new List<TrendyolBuybox>();foreach(var batch in profiles.Select(p=>p.IntegrationCode).Chunk(10))results.AddRange(await client.GetBuyboxAsync(batch,Token));EnsureAccount(account);
            var local=catalog.Products().ToDictionary(p=>p.Id);competition.ItemsSource=profiles.Select(p=>{var result=results.SingleOrDefault(r=>r.Barcode==p.IntegrationCode);var usedMin=min??p.CompetitionMinimum??0;var usedMax=max??p.CompetitionMaximum??0;var usedDiff=diff??p.CompetitionDifference;var suggestion=result==null?new TrendyolPriceSuggestion(null,"API yanıtında bu barkod yok."):TrendyolCompetition.Suggest(result,usedMin,usedMax,usedDiff);return new TrendyolCompetitionRow(p.ProductId,local[p.ProductId].Sku,p.IntegrationCode,result?.Rank,result?.FirstPrice,result?.SecondPrice,result?.ThirdPrice,suggestion.Price,suggestion.Reason,usedMin,usedMax,usedDiff);}).ToList();status.Text="Rekabet verisi alındı. Fiyatlar henüz değiştirilmedi.";
        }));actions.Children.Add(B("Seçili önerileri fiyat profiline al",()=>{
            var selected=competition.SelectedItems.Cast<TrendyolCompetitionRow>().ToArray();if(selected.Length==0||selected.Any(r=>!r.Suggested.HasValue))throw new InvalidOperationException("Fiyat önerisi olan satırları seçin.");var account=Account();if(competitionAccount!=TrendyolWorkspaceStore.AccountFingerprint(account))throw new InvalidOperationException("Rekabet önerisi farklı hesaba ait; bilgileri yeniden alın.");var next=store.Load(account.SupplierId);if(next.Revision!=competitionRevision)throw new InvalidOperationException("Rekabet önerisi eskidi; bilgileri yeniden alın.");
            if(MessageBox.Show(Window.GetWindow(this),string.Join("\n",selected.Select(r=>$"{r.Sku}: {r.Suggested} TRY"))+"\nProfil fiyatları kaydedilsin mi? Gönderim için Ürünler'de yalnız fiyat önizlemesi gereklidir.","Rekabet fiyatı önizlemesi",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            foreach(var row in selected){var profile=next.Profiles.Single(p=>p.ProductId==row.ProductId);profile.SalePriceTry=row.Suggested;profile.ListPriceTry=Math.Max(profile.ListPriceTry??row.Suggested!.Value,row.Suggested!.Value);profile.CompetitionMinimum=row.Minimum;profile.CompetitionMaximum=row.Maximum;profile.CompetitionDifference=row.Difference;}store.Save(next);Reload();status.Text="Öneriler profillere alındı; Trendyol'a henüz gönderilmedi.";
        }));top.Children.Add(actions);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(competition);return dock;
    }
    FrameworkElement BuildHistory()
    {
        var dock=new DockPanel();var top=new StackPanel();var actions=new WrapPanel();actions.Children.Add(B("Geçmişi yenile",ReloadHistory));actions.Children.Add(A("Seçili işlemin API sonucunu sorgula",async()=>{var row=history.SelectedItem as TrendyolReceipt??throw new InvalidOperationException("İşlem seçin.");if(row.BatchId.Length==0)throw new InvalidOperationException("Bu gönderimde batch ID alınamadı; mağazada kontrol edin.");var account=Account();if(account.SupplierId!=row.SellerId)throw new InvalidOperationException("Hesap değişti.");using var client=new TrendyolApiClient(account);var result=await client.GetBatchAsync(row.BatchId,Token);EnsureAccount(account);store.UpdateBatch(row.PlanId,row.SellerId,result);ReloadHistory();status.Text="Toplu işlem sonucu yenilendi. İşlem tamamlanması ürünün yayında olduğunu göstermez; mağaza ürünlerini yeniden çekin.";}));top.Children.Add(actions);
        top.Children.Add(T("API batch sonuçları yaklaşık 4 saat erişilebilir. Kuyrukta / işleniyor / hatalı durumları ayrı izlenir. Belirsiz istekler otomatik tekrarlanmaz.",true));
        var resolve=new WrapPanel();resolve.Children.Add(T("Mağazada yaptığınız kontrol"));resolutionNote.Width=360;resolve.Children.Add(resolutionNote);resolve.Children.Add(B("Mağazada kontrol ettim, kaydet",()=>{var row=history.SelectedItem as TrendyolReceipt??throw new InvalidOperationException("İşlem seçin.");if(MessageBox.Show(Window.GetWindow(this),"Trendyol satıcı panelinde bu işlemin sonucunu kontrol ettiğinizi onaylıyor musunuz? Yeni gönderim ayrıca yeni önizleme gerektirir.","Belirsiz gönderim kontrolü",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;store.ResolveUnknown(row.PlanId,Account().SupplierId,resolutionNote.Text);ReloadHistory();}));top.Children.Add(new Expander{Header="Belirsiz gönderim için mağaza kontrolü",Content=resolve,Margin=new(5)});DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);DockPanel.SetDock(historyDetail,Dock.Bottom);dock.Children.Add(historyDetail);dock.Children.Add(history);history.SelectionChanged+=(_,_)=>historyDetail.Text=(history.SelectedItem as TrendyolReceipt)?.Detail??"";return dock;
    }
    void ReloadHistory(){history.ItemsSource=state.SellerId.Length>0?store.Receipts(state.SellerId):Array.Empty<TrendyolReceipt>();}
}
