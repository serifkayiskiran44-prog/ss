using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrMarketplaceHubDesktop.Catalog;
using ClosedXML.Excel;
using Microsoft.Win32;
namespace TrMarketplaceHubDesktop;
public sealed class TaxonomyListRow
{
 public TaxonomyEntry Entry { get; init; }=new();
 public string Id=>Entry.Id;
 public string Path=>Entry.Name;
 public string Label { get; set; }="";
 public bool Active { get; set; }
 public int Children { get; init; }
 public int Products { get; init; }
 public string ScopeShop {get;set;}="";
 public List<TaxonomyMapping> OriginalMappings {get;set;}=new();
 public Dictionary<string,string> OriginalChannels {get;}=new();
 public Dictionary<string,string> Channels {get;init;}=new();
 public string TrendyolApiId {get;set;}="";
}
public sealed partial class TaxonomyWorkspacePanel
{
 readonly ComboBox bulkParent=new(){Width=270,DisplayMemberPath="Name",IsEditable=false};
 readonly ComboBox stateFilter=new(){Width=115,ItemsSource=new[]{"Tüm durumlar","Aktif","Pasif"},SelectedIndex=0};
 readonly CheckBox quickEdit=new(){Content="Düzenleme aktif",Margin=new(8),VerticalAlignment=VerticalAlignment.Center};
 List<TaxonomyListRow> listRows=new();
 static readonly string[] ListChannels={"Trendyol","Hepsiburada","N11","Amazon","AliExpress","Etsy","E-Ticaret"};
 FrameworkElement BuildToolbar()
 {
  var toolbar=new StackPanel();var actions=new WrapPanel();toolbar.Children.Add(actions);
  actions.Children.Add(B("Ekle",()=>{Edit(null);tabs.SelectedIndex=1;}));actions.Children.Add(B("Düzenle",()=>{RequireSelection();tabs.SelectedIndex=1;}));
  actions.Children.Add(B("Seçilenleri sil",()=>ApplyBatch(TaxonomyBatchAction.Delete)));
  actions.Children.Add(B("Aktif yap",()=>ApplyBatch(TaxonomyBatchAction.Activate)));actions.Children.Add(B("Pasif yap",()=>ApplyBatch(TaxonomyBatchAction.Deactivate)));
  if(kind==TaxonomyKind.Category)actions.Children.Add(B("Onar",()=>ApplyBatch(TaxonomyBatchAction.Repair)));
  actions.Children.Add(B(kind==TaxonomyKind.Category?"Default / şablon":"Marka karşılıkları",()=>{RequireSelection();tabs.SelectedIndex=kind==TaxonomyKind.Category?3:2;}));
  actions.Children.Add(B("Excel'e aktar",ExportList));
  var filters=new WrapPanel();toolbar.Children.Add(filters);filters.Children.Add(T(kind==TaxonomyKind.Category?"Kategori ara":"Marka ara"));filters.Children.Add(search);filters.Children.Add(stateFilter);filters.Children.Add(B("Yenile ve filtrele",Reload));
  filters.Children.Add(B("Tümünü seç",()=>list.SelectAll()));filters.Children.Add(B("Seçimi kaldır",()=>list.UnselectAll()));filters.Children.Add(B("Seçilendeki ürün sayısı",ShowSelectedCount));
  filters.Children.Add(quickEdit);filters.Children.Add(B("Düzenlemeleri kaydet",SaveQuickEdits));
  if(kind==TaxonomyKind.Category){var attach=new WrapPanel();toolbar.Children.Add(attach);attach.Children.Add(T("Hedef üst kategori"));attach.Children.Add(bulkParent);attach.Children.Add(B("Seçilenleri kategoriye bağla",()=>ApplyBatch(TaxonomyBatchAction.Attach)));}
  search.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter)Reload();};
  quickEdit.Checked+=(_,_)=>list.IsReadOnly=false;quickEdit.Unchecked+=(_,_)=>{list.CommitEdit(DataGridEditingUnit.Cell,true);list.CommitEdit(DataGridEditingUnit.Row,true);list.IsReadOnly=true;};
  list.BeginningEdit+=(_,e)=>{var market=e.Column.Header as string;if(market!=null&&ListChannels.Contains(market)&&e.Row.Item is TaxonomyListRow row&&row.OriginalMappings.Count(m=>m.Marketplace.Equals(market,StringComparison.OrdinalIgnoreCase))>1){e.Cancel=true;status.Text="Bu kaydın birden fazla pazaryeri karşılığı var. Pazaryeri eşleştirmeleri sekmesinde tek tek düzenleyin.";}};
  list.Columns.Add(new DataGridTextColumn{Header="ID",Binding=new Binding("Id"),Width=80,IsReadOnly=true});
  list.Columns.Add(new DataGridCheckBoxColumn{Header="Aktif",Binding=new Binding("Active"),Width=55});
  if(kind==TaxonomyKind.Category)list.Columns.Add(new DataGridTextColumn{Header="Kategori ağacı",Binding=new Binding("Path"),Width=300,IsReadOnly=true});
  list.Columns.Add(new DataGridTextColumn{Header=kind==TaxonomyKind.Category?"Kategori adı":"Marka adı",Binding=new Binding("Label"),Width=200});
  if(kind==TaxonomyKind.Category)list.Columns.Add(new DataGridTextColumn{Header="Alt kategori",Binding=new Binding("Children"),Width=90,IsReadOnly=true});
  list.Columns.Add(new DataGridTextColumn{Header="Ürün sayısı",Binding=new Binding("Products"),Width=90,IsReadOnly=true});
  foreach(var market in ListChannels)list.Columns.Add(new DataGridTextColumn{Header=market,Binding=new Binding("Channels["+market+"]"),Width=130});
  list.Columns.Add(new DataGridTextColumn{Header="Trendyol API ID (satıcı)",Binding=new Binding("TrendyolApiId"),Width=180,IsReadOnly=true});
  return toolbar;
 }
 void ReloadRows(string? keep)
 {
  var query=search.Text.Trim();listRows=entries.Where(e=>(query.Length==0||e.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase))&&(stateFilter.SelectedIndex==0||e.Active==(stateFilter.SelectedIndex==1))).Select(e=>new TaxonomyListRow{Entry=e,Label=kind==TaxonomyKind.Category?e.Name.Split(" > ").Last():e.Name,Active=e.Active,Children=kind==TaxonomyKind.Category?entries.Count(other=>other.Id!=e.Id&&TaxonomyStore.InCategory(other.Name,e.Name)):0,Products=products.Count(p=>kind==TaxonomyKind.Category?TaxonomyStore.InCategory(p.Category,e.Name):p.Brand.Equals(e.Name,StringComparison.OrdinalIgnoreCase))}).ToList();
  list.ItemsSource=listRows;RefreshChannelColumns();bulkParent.ItemsSource=new[]{new TaxonomyEntry{Id="",Name="(Ana kategori)"}}.Concat(entries).ToList();bulkParent.SelectedIndex=0;
  list.SelectedItem=listRows.FirstOrDefault(r=>r.Id==keep);if(list.SelectedItem==null)Edit(null);
 }
 void RefreshChannelColumns()
 {
  try{var account=new TrendyolSettingsStore(workspaceDirectory is null?null:System.IO.Path.Combine(workspaceDirectory,"trendyol.bin")).Load();var api=account is null?null:new Trendyol.TrendyolWorkspaceStore(workspaceDirectory).Load(account.SupplierId);foreach(var row in listRows){var target=api?.Mappings.SingleOrDefault(m=>m.Kind==kind&&m.LocalId==row.Id);row.TrendyolApiId=target is null?"":$"{target.RemoteId} ({api!.SellerId})"+(target.LocalName!=row.Path?" · yenileyin":"");}}catch{foreach(var row in listRows)row.TrendyolApiId="Bağlantıyı kontrol edin";}
  var all=store.Mappings(kind);foreach(var row in listRows){row.ScopeShop=Shop;row.OriginalMappings=all.Where(m=>m.LocalId==row.Id&&m.ShopId==Shop).ToList();foreach(var market in ListChannels){var value=string.Join(", ",row.OriginalMappings.Where(m=>m.Marketplace.Equals(market,StringComparison.OrdinalIgnoreCase)).Select(m=>m.ExternalKey));row.Channels[market]=value;row.OriginalChannels[market]=value;}}
  if(!list.IsKeyboardFocusWithin)list.Items.Refresh();
 }
 List<TaxonomyEntry> RequireSelection()
 {
  var selectedRows=list.SelectedItems.Cast<TaxonomyListRow>().Select(r=>r.Entry).ToList();if(selectedRows.Count==0)throw new InvalidOperationException("Listeden en az bir kayıt seçin.");return selectedRows;
 }
 void ApplyBatch(TaxonomyBatchAction action)
 {
  var selection=RequireSelection();var target=bulkParent.SelectedItem as TaxonomyEntry;
  var label=action switch{TaxonomyBatchAction.Delete=>"silme",TaxonomyBatchAction.Activate=>"aktif yapma",TaxonomyBatchAction.Deactivate=>"pasif yapma",TaxonomyBatchAction.Attach=>"üst kategoriye bağlama",_=>"kategori yollarını onarma"};
  var detail=action==TaxonomyBatchAction.Attach?$"\nHedef: {target?.Name}\nAlt kategoriler ve bağlı ürün yolları birlikte taşınır.":action==TaxonomyBatchAction.Delete?"\nÜrün veya pazaryeri bağlantısı bulunan kayıt varsa hiçbir kayıt silinmez.":"";
  var names=string.Join("\n",selection.Take(8).Select(e=>e.Name));
  if(MessageBox.Show(Window.GetWindow(this),$"{selection.Count} kayıt için {label}:\n{names}"+(selection.Count>8?"\n…":"")+detail,"Toplu işlem önizlemesi",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
  store.ApplyWorkspaceBatch(kind,selection,action,target?.Id);changed();Reload();status.Text=$"{selection.Count} kayıt için {label} tamamlandı.";
 }
 void SaveQuickEdits()
 {
  list.CommitEdit(DataGridEditingUnit.Cell,true);list.CommitEdit(DataGridEditingUnit.Row,true);
  var changedRows=listRows.Where(r=>r.Active!=r.Entry.Active||r.Label!=(kind==TaxonomyKind.Category?r.Entry.Name.Split(" > ").Last():r.Entry.Name)||ListChannels.Any(m=>r.Channels.GetValueOrDefault(m,"")!=r.OriginalChannels.GetValueOrDefault(m,""))).ToList();
  if(changedRows.Count==0){status.Text="Kaydedilecek düzenleme yok. Düzenleme aktif ile ad ve aktiflik hücrelerini değiştirebilirsiniz.";return;}
  var edits=new List<TaxonomyWorkspaceEdit>();foreach(var row in changedRows){if(row.ScopeShop!=Shop)throw new InvalidOperationException("Mağaza değişti; listeyi yenileyin.");if(row.Label.Contains('>'))throw new InvalidOperationException("Kategori taşımak için Seçilenleri kategoriye bağla işlemini kullanın.");var split=row.Entry.Name.LastIndexOf(" > ",StringComparison.Ordinal);var path=kind==TaxonomyKind.Category&&split>0?row.Entry.Name[..(split+3)]+row.Label:row.Label;var channelEdits=ListChannels.Where(m=>row.Channels.GetValueOrDefault(m,"")!=row.OriginalChannels.GetValueOrDefault(m,"")).Select(m=>new TaxonomyWorkspaceMappingEdit(m,Shop,row.Channels.GetValueOrDefault(m,""),row.OriginalMappings.Where(old=>old.Marketplace.Equals(m,StringComparison.OrdinalIgnoreCase)).ToList())).ToList();edits.Add(new(new(){Id=row.Id,Kind=kind,Name=path,Value=row.Entry.Value,Active=row.Active,UpdatedUtc=row.Entry.UpdatedUtc},channelEdits));}
  if(MessageBox.Show(Window.GetWindow(this),string.Join("\n",changedRows.Take(10).Select(r=>$"{r.Entry.Name} → {r.Label} ({(r.Active?"aktif":"pasif")})"+string.Concat(ListChannels.Where(m=>r.Channels.GetValueOrDefault(m,"")!=r.OriginalChannels.GetValueOrDefault(m,"")).Select(m=>$"\n  {m}: {r.OriginalChannels[m]} → {r.Channels[m]}"))+ $""))+$"\n{edits.Count} kaydın düzenlemesi uygulansın mı?","Düzenleme önizlemesi",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
  store.SaveWorkspaceEdits(edits);changed();Reload();status.Text=$"{edits.Count} kayıt güncellendi.";
 }
 void ShowSelectedCount()
 {
  var rows=RequireSelection();var count=products.Count(p=>rows.Any(e=>kind==TaxonomyKind.Category?TaxonomyStore.InCategory(p.Category,e.Name):p.Brand.Equals(e.Name,StringComparison.OrdinalIgnoreCase)));status.Text=$"{rows.Count} seçili kayıtta {count} farklı ürün var. Üst/alt kategori tekrarları bir kez sayıldı.";
 }
 void CopyTemplateToSelected()
 {
  var rows=RequireSelection();var original=Capture();templates.Preview(original,new CatalogProduct());
  if(MessageBox.Show(Window.GetWindow(this),$"{rows.Count} seçili kaydın {Channel}/{Shop} şablonu değiştirilsin mi?","Şablon önizlemesi",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
  var copies=new List<TaxonomyContentTemplate>();foreach(var entry in rows){var t=System.Text.Json.JsonSerializer.Deserialize<TaxonomyContentTemplate>(System.Text.Json.JsonSerializer.Serialize(original))!;t.EntryId=entry.Id;t.Version=templates.Get(entry.Id,Channel,Shop)?.Version??0;copies.Add(t);}templates.SaveBatch(copies);LoadTemplate();status.Text=$"Şablon {rows.Count} kayda uygulandı.";
 }
 void ExportList()
 {
  var dialog=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName=kind==TaxonomyKind.Category?"kategoriler.xlsx":"markalar.xlsx"};if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
  var selectedRows=list.SelectedItems.Cast<TaxonomyListRow>().ToList();var export=selectedRows.Count>0?selectedRows:listRows;using var book=new XLWorkbook();var sheet=book.AddWorksheet("Liste");var headers=new[]{"ID","Aktif","Kategori / Marka","Ad","Alt kategori","Ürün sayısı"}.Concat(ListChannels).ToArray();for(int i=0;i<headers.Length;i++)sheet.Cell(1,i+1).Value=headers[i];var n=2;foreach(var row in export){sheet.Cell(n,1).Value=row.Id;sheet.Cell(n,2).Value=row.Active;sheet.Cell(n,3).Value=row.Path;sheet.Cell(n,4).Value=row.Label;sheet.Cell(n,5).Value=row.Children;sheet.Cell(n,6).Value=row.Products;for(int i=0;i<ListChannels.Length;i++)sheet.Cell(n,7+i).Value=row.Channels.GetValueOrDefault(ListChannels[i],"");n++;}sheet.SheetView.FreezeRows(1);sheet.Columns().AdjustToContents(10,60);book.SaveAs(dialog.FileName);status.Text=$"{export.Count} kayıt Excel'e aktarıldı.";
 }
}
